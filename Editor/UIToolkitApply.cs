using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    [Serializable]
    internal sealed class UIToolkitApplyChangeRequest
    {
        public string path;
        public string content;
        public string expectedHash;
    }

    [Serializable]
    internal sealed class UIToolkitApplyRequest
    {
        public string mode;
        public UIToolkitApplyChangeRequest[] changes;
        public string expectedHash;
        public int maxIssues;
    }

    [Serializable]
    internal sealed class UIToolkitPlannedChange
    {
        public string path;
        public string operation;
        public string currentHash;
        public string proposedHash;
        public int bytesBefore;
        public int bytesAfter;
        public int addedLines;
        public int removedLines;
    }

    [Serializable]
    internal sealed class UIToolkitApplyPlanResponse
    {
        public bool ok;
        public string status;
        public string mode;
        public string planHash;
        public List<UIToolkitPlannedChange> changes;
        public UIToolkitValidationResponse validation;
        public UIToolkitApplyError error;
        public string[] limits;
    }

    [Serializable]
    internal sealed class UIToolkitApplyError
    {
        public string code;
        public string message;
        public string recovery;
    }

    [Serializable]
    internal sealed class UIToolkitApplyCommitResponse
    {
        public bool ok;
        public string status;
        public string mode;
        public string planHash;
        public List<string> committed;
        public List<string> imported;
        public string rollback;
        public string[] limits;
    }

    internal sealed class UIToolkitPreparedChange
    {
        public UIToolkitFileSnapshot Current;
        public byte[] ProposedBytes;
        public string ProposedContent;
        public UIToolkitPlannedChange Plan;
        public string ExpectedHash;
        public string TemporaryPath;
        public string BackupPath;
        public bool WasCommitted;
    }

    internal static class UIToolkitApply
    {
        const string PlanContractVersion = "uitk-apply-v1";
        const int MaximumChanges = 8;

        internal static string Execute(string body)
        {
            if (!TryParseRequest(body, out UIToolkitApplyRequest request, out string parseError))
                return UIToolkitJson.Error("INVALID_REQUEST", parseError, "Send a valid apply request with mode and one to eight changes.");

            string mode = string.IsNullOrEmpty(request.mode) ? "plan" : request.mode.ToLowerInvariant();
            if (mode != "plan" && mode != "commit")
                return UIToolkitJson.Error("INVALID_REQUEST", $"Unknown apply mode '{request.mode}'.", "Use mode 'plan' or 'commit'.");

            if (!TryBuildPlan(request, out List<UIToolkitPreparedChange> prepared, out UIToolkitValidationResponse validation, out string planHash, out string code, out string message))
                return UIToolkitJson.Error(code, message, "Inspect current hashes, correct the proposed source, and create a new plan.");

            if (!validation.ok)
            {
                return BuildPlanResponse(prepared, validation, planHash, new UIToolkitApplyError
                {
                    code = "VALIDATION_FAILED",
                    message = "Proposed UI Toolkit source contains validation errors.",
                    recovery = "Review validation.issues, correct the proposed content, and create a new plan.",
                });
            }

            if (mode == "plan")
                return BuildPlanResponse(prepared, validation, planHash);

            if (string.IsNullOrEmpty(request.expectedHash))
                return UIToolkitJson.Error("HASH_REQUIRED", "Commit mode requires the top-level expectedHash from the matching plan.", "Run mode 'plan', then send the same changes with expectedHash set to the returned planHash.");
            if (!string.Equals(request.expectedHash, planHash, StringComparison.Ordinal))
                return UIToolkitJson.Error("PLAN_HASH_MISMATCH", "The supplied plan hash does not match these changes and current file hashes.", "Generate a fresh plan and commit its unchanged request body with the returned planHash.");
            if (!MCPHandlers.AllowWrites)
                return UIToolkitJson.Error("READ_ONLY", "Allow Write Commands is OFF immediately before the UI Toolkit commit.", "Enable AI Unity MCP Server/Allow Write Commands in Unity and retry the unchanged commit request.");

            return Commit(prepared, planHash);
        }

        static bool TryParseRequest(string body, out UIToolkitApplyRequest request, out string error)
        {
            request = null;
            error = null;
            if (string.IsNullOrWhiteSpace(body))
            {
                error = "The request body is empty.";
                return false;
            }
            try
            {
                request = JsonUtility.FromJson<UIToolkitApplyRequest>(body);
            }
            catch (Exception exception)
            {
                error = $"The request JSON is malformed: {exception.Message}";
                return false;
            }
            if (request == null)
            {
                error = "The request JSON could not be parsed.";
                return false;
            }
            return true;
        }

        static bool TryBuildPlan(
            UIToolkitApplyRequest request,
            out List<UIToolkitPreparedChange> prepared,
            out UIToolkitValidationResponse validation,
            out string planHash,
            out string code,
            out string message)
        {
            prepared = new List<UIToolkitPreparedChange>();
            validation = null;
            planHash = null;
            code = null;
            message = null;

            if (request.changes == null || request.changes.Length == 0 || request.changes.Length > MaximumChanges)
            {
                code = "INVALID_REQUEST";
                message = $"Apply requires between 1 and {MaximumChanges} changes.";
                return false;
            }

            var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalBytes = 0;
            foreach (UIToolkitApplyChangeRequest change in request.changes)
            {
                if (change == null || string.IsNullOrEmpty(change.path) || change.content == null)
                {
                    code = "INVALID_REQUEST";
                    message = "Every change requires path, content, and expectedHash fields.";
                    return false;
                }
                if (string.IsNullOrEmpty(change.expectedHash))
                {
                    code = "HASH_REQUIRED";
                    message = $"Change '{change.path}' requires expectedHash. Use 'missing' only when intentionally creating a new file.";
                    return false;
                }
                if (!UIToolkitPathGuard.TryResolve(change.path, false, out UIToolkitFileSnapshot current, out code, out message))
                    return false;
                if (!canonicalPaths.Add(current.AssetPath))
                {
                    code = "INVALID_REQUEST";
                    message = $"The change list contains duplicate path '{current.AssetPath}'.";
                    return false;
                }

                if (current.Exists)
                {
                    if (!UIToolkitPathGuard.TryRead(current.AssetPath, UIToolkitPathGuard.MaximumApplyFileBytes, out current, out code, out message))
                    {
                        if (code != "UNSAFE_PATH" && code != "NOT_FOUND") code = "INVALID_REQUEST";
                        return false;
                    }
                }

                string currentHash = current.Exists ? current.Hash : "missing";
                if (!string.Equals(change.expectedHash, currentHash, StringComparison.Ordinal))
                {
                    code = "STALE_SOURCE";
                    message = $"'{current.AssetPath}' has hash '{currentHash}', not the expected hash '{change.expectedHash}'.";
                    return false;
                }

                byte[] proposedBytes = UIToolkitPathGuard.Encode(change.content);
                if (proposedBytes.Length > UIToolkitPathGuard.MaximumApplyFileBytes)
                {
                    code = "INVALID_REQUEST";
                    message = $"'{current.AssetPath}' is {proposedBytes.Length} bytes; each proposed file is limited to {UIToolkitPathGuard.MaximumApplyFileBytes} bytes.";
                    return false;
                }
                totalBytes += proposedBytes.Length;
                if (totalBytes > UIToolkitPathGuard.MaximumApplyTotalBytes)
                {
                    code = "INVALID_REQUEST";
                    message = $"Proposed contents exceed the {UIToolkitPathGuard.MaximumApplyTotalBytes}-byte request limit.";
                    return false;
                }

                CountLineChanges(current.Text, change.content, out int addedLines, out int removedLines);
                string proposedHash = UIToolkitPathGuard.ComputeHash(proposedBytes);
                prepared.Add(new UIToolkitPreparedChange
                {
                    Current = current,
                    ProposedBytes = proposedBytes,
                    ProposedContent = change.content,
                    ExpectedHash = change.expectedHash,
                    Plan = new UIToolkitPlannedChange
                    {
                        path = current.AssetPath,
                        operation = !current.Exists ? "create" : currentHash == proposedHash ? "unchanged" : "update",
                        currentHash = currentHash,
                        proposedHash = proposedHash,
                        bytesBefore = current.Bytes?.Length ?? 0,
                        bytesAfter = proposedBytes.Length,
                        addedLines = addedLines,
                        removedLines = removedLines,
                    },
                });
            }

            var virtualContents = prepared.ToDictionary(item => item.Current.AssetPath, item => item.ProposedContent, StringComparer.Ordinal);
            validation = UIToolkitValidator.ValidateChanges(virtualContents, request.maxIssues);
            planHash = ComputePlanHash(prepared);
            return true;
        }

        static string BuildPlanResponse(
            List<UIToolkitPreparedChange> prepared,
            UIToolkitValidationResponse validation,
            string planHash,
            UIToolkitApplyError error = null)
        {
            var response = new UIToolkitApplyPlanResponse
            {
                ok = error == null,
                status = error != null ? "blocked" : validation.status == "partial" ? "partial" : "complete",
                mode = "plan",
                planHash = planHash,
                changes = prepared.Select(item => item.Plan).OrderBy(item => item.path, StringComparer.Ordinal).ToList(),
                validation = validation,
                error = error,
                limits = new[]
                {
                    "Plan mode is read-only and does not create temporary files or import assets.",
                    "Commit uses adjacent temporary and backup files with rollback, but does not claim filesystem-wide atomicity.",
                },
            };
            return JsonUtility.ToJson(response);
        }

        static string Commit(List<UIToolkitPreparedChange> prepared, string planHash)
        {
            string transactionId = Guid.NewGuid().ToString("N");
            try
            {
                PrepareTemporaryFiles(prepared, transactionId);
                foreach (UIToolkitPreparedChange change in prepared)
                {
                    if (change.Plan.operation == "unchanged")
                        continue;
                    if (!MCPHandlers.AllowWrites)
                        throw new InvalidOperationException("READ_ONLY: Allow Write Commands was disabled before all changes were committed.");
                    CommitFile(change);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                VerifyCommittedHashes(prepared);
                DeleteTransactionFiles(prepared);
                var committed = prepared.Where(item => item.Plan.operation != "unchanged").Select(item => item.Current.AssetPath).ToList();
                return JsonUtility.ToJson(new UIToolkitApplyCommitResponse
                {
                    ok = true,
                    status = "complete",
                    mode = "commit",
                    planHash = planHash,
                    committed = committed,
                    imported = new List<string>(committed),
                    rollback = "not-needed",
                    limits = new[]
                    {
                        "Files were replaced with adjacent temporary and backup files and verified after one AssetDatabase refresh.",
                        "This is an all-or-rollback workflow, not a claim of filesystem-wide atomicity.",
                    },
                });
            }
            catch (Exception commitException)
            {
                bool rollbackSucceeded = TryRollback(prepared, out string rollbackMessage);
                if (!rollbackSucceeded)
                {
                    return UIToolkitJson.Error(
                        "ROLLBACK_FAILED",
                        $"Commit failed and rollback was incomplete: {commitException.Message}",
                        "Stop editing the affected UI files and restore them from version control or the reported adjacent backup files.",
                        rollbackMessage);
                }
                DeleteTransactionFiles(prepared);
                return UIToolkitJson.Error(
                    commitException.Message.StartsWith("READ_ONLY:", StringComparison.Ordinal) ? "READ_ONLY" : "COMMIT_FAILED",
                    $"No UI Toolkit changes remain committed: {commitException.Message}",
                    "Resolve the reported cause, generate a fresh plan, and retry commit.",
                    "Rollback completed successfully.");
            }
        }

        static void PrepareTemporaryFiles(List<UIToolkitPreparedChange> prepared, string transactionId)
        {
            foreach (UIToolkitPreparedChange change in prepared)
            {
                if (change.Plan.operation == "unchanged")
                    continue;
                string directory = Path.GetDirectoryName(change.Current.AbsolutePath);
                string fileName = Path.GetFileName(change.Current.AbsolutePath);
                change.TemporaryPath = Path.Combine(directory, $".{fileName}.aimcp.{transactionId}.tmp");
                change.BackupPath = Path.Combine(directory, $".{fileName}.aimcp.{transactionId}.bak");
                File.WriteAllBytes(change.TemporaryPath, change.ProposedBytes);
            }
        }

        static void CommitFile(UIToolkitPreparedChange change)
        {
            if (change.Current.Exists)
                File.Replace(change.TemporaryPath, change.Current.AbsolutePath, change.BackupPath, true);
            else
                File.Move(change.TemporaryPath, change.Current.AbsolutePath);
            change.WasCommitted = true;
        }

        static void VerifyCommittedHashes(List<UIToolkitPreparedChange> prepared)
        {
            foreach (UIToolkitPreparedChange change in prepared)
            {
                if (!File.Exists(change.Current.AbsolutePath))
                    throw new IOException($"Verification could not find '{change.Current.AssetPath}'.");
                string actualHash = UIToolkitPathGuard.ComputeHash(File.ReadAllBytes(change.Current.AbsolutePath));
                if (!string.Equals(actualHash, change.Plan.proposedHash, StringComparison.Ordinal))
                    throw new IOException($"Verification hash mismatch for '{change.Current.AssetPath}'.");
            }
        }

        static bool TryRollback(List<UIToolkitPreparedChange> prepared, out string message)
        {
            var failures = new List<string>();
            foreach (UIToolkitPreparedChange change in prepared.AsEnumerable().Reverse())
            {
                if (!change.WasCommitted)
                    continue;
                try
                {
                    if (change.Current.Exists)
                    {
                        if (!File.Exists(change.BackupPath))
                            throw new FileNotFoundException("Adjacent backup is missing.", change.BackupPath);
                        File.Copy(change.BackupPath, change.Current.AbsolutePath, true);
                    }
                    else if (File.Exists(change.Current.AbsolutePath))
                    {
                        File.Delete(change.Current.AbsolutePath);
                        string metaPath = change.Current.AbsolutePath + ".meta";
                        if (File.Exists(metaPath)) File.Delete(metaPath);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"{change.Current.AssetPath}: {exception.Message}");
                }
            }
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            catch (Exception exception)
            {
                failures.Add("AssetDatabase refresh: " + exception.Message);
            }
            message = failures.Count == 0 ? "Rollback completed." : string.Join(" | ", failures);
            return failures.Count == 0;
        }

        static void DeleteTransactionFiles(IEnumerable<UIToolkitPreparedChange> prepared)
        {
            foreach (UIToolkitPreparedChange change in prepared)
            {
                TryDelete(change.TemporaryPath);
                TryDelete(change.BackupPath);
            }
        }

        static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[AI Unity MCP Server] Could not remove UI Toolkit transaction file '{path}': {exception.Message}");
            }
        }

        static string ComputePlanHash(IEnumerable<UIToolkitPreparedChange> prepared)
        {
            var builder = new StringBuilder(PlanContractVersion);
            foreach (UIToolkitPreparedChange change in prepared.OrderBy(item => item.Current.AssetPath, StringComparer.Ordinal))
            {
                builder.Append('\n').Append(change.Current.AssetPath);
                builder.Append('\n').Append(change.Plan.currentHash);
                builder.Append('\n').Append(change.ExpectedHash);
                builder.Append('\n').Append(change.Plan.proposedHash);
            }
            return UIToolkitPathGuard.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        static void CountLineChanges(string current, string proposed, out int added, out int removed)
        {
            string[] before = SplitLines(current ?? string.Empty);
            string[] after = SplitLines(proposed ?? string.Empty);
            int prefix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            int suffix = 0;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
                   before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
            removed = before.Length - prefix - suffix;
            added = after.Length - prefix - suffix;
        }

        static string[] SplitLines(string value)
        {
            if (value.Length == 0) return Array.Empty<string>();
            return value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}

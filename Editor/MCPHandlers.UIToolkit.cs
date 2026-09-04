using System;

namespace AIUnityMCPServer
{
    public static partial class MCPHandlers
    {
        static string InspectUIToolkit(string body)
        {
            UIToolkitInspectRequest request = ParseReq<UIToolkitInspectRequest>(body);
            if (string.IsNullOrWhiteSpace(request.path))
                return UIToolkitJson.Error("INVALID_REQUEST", "Inspect requires path.", "Pass a canonical Assets/... .uxml or .uss path.");
            return ExecuteOnMainThread(() => UIToolkitSource.Inspect(
                request.path,
                request.includeLinkedStyles,
                request.maxNodes,
                request.maxDepth,
                request.maxSelectors));
        }

        static string ValidateUIToolkit(string body)
        {
            UIToolkitValidateRequest request = ParseReq<UIToolkitValidateRequest>(body);
            if (string.IsNullOrWhiteSpace(request.path))
                return UIToolkitJson.Error("INVALID_REQUEST", "Validate requires path.", "Pass a canonical Assets/... .uxml or .uss path.");
            bool includeLinkedStyles = request.includeLinkedStyles || !HasJsonProperty(body, "includeLinkedStyles");
            return ExecuteOnMainThread(() => UIToolkitValidator.Validate(request.path, includeLinkedStyles, request.maxIssues));
        }

        static string ApplyUIToolkit(string body)
        {
            return ExecuteOnMainThread(() => UIToolkitApply.Execute(body));
        }

        static string PlaytestUIToolkit(string body)
        {
            return ExecuteOnMainThread(() => UIToolkitPlaytest.Execute(body));
        }

        static bool HasJsonProperty(string body, string property)
        {
            return !string.IsNullOrEmpty(body) && body.IndexOf("\"" + property + "\"", StringComparison.Ordinal) >= 0;
        }

        [Serializable]
        sealed class UIToolkitInspectRequest
        {
            public string path;
            public bool includeLinkedStyles;
            public int maxNodes;
            public int maxDepth;
            public int maxSelectors;
        }

        [Serializable]
        sealed class UIToolkitValidateRequest
        {
            public string path;
            public bool includeLinkedStyles;
            public int maxIssues;
        }
    }
}

using NUnit.Framework;
using UnityEngine;

namespace MCPBridge.Tests
{
    public class UnityObjectIdTests
    {
        [Test]
        public void GetResponseInstanceIdIsAPlainJsonNumber()
        {
            var gameObject = new GameObject("MCP Bridge Object ID Test");

            try
            {
                StringAssert.IsMatch("^-?[0-9]+$", MCPHandlers.GetResponseInstanceId(gameObject));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GetResponseInstanceIdStaysTheSameAcrossReads()
        {
            var gameObject = new GameObject("MCP Bridge Object ID Test");

            try
            {
                string firstRead = MCPHandlers.GetResponseInstanceId(gameObject);
                string secondRead = MCPHandlers.GetResponseInstanceId(gameObject);

                Assert.AreEqual(firstRead, secondRead);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GetResponseInstanceIdTellsTwoObjectsApart()
        {
            var firstObject = new GameObject("MCP Bridge Object ID Test A");
            var secondObject = new GameObject("MCP Bridge Object ID Test B");

            try
            {
                Assert.AreNotEqual(
                    MCPHandlers.GetResponseInstanceId(firstObject),
                    MCPHandlers.GetResponseInstanceId(secondObject));
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }
    }
}

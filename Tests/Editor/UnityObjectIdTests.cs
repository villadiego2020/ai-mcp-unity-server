using NUnit.Framework;
using UnityEngine;

namespace MCPBridge.Tests
{
    public class UnityObjectIdTests
    {
        [Test]
        public void GetResponseInstanceIdReturnsTheUnityObjectIdentifier()
        {
            var gameObject = new GameObject("MCP Bridge Object ID Test");

            try
            {
                string responseInstanceId = MCPHandlers.GetResponseInstanceId(gameObject);

#if UNITY_6000_5_OR_NEWER
                ulong expectedInstanceId = EntityId.ToULong(gameObject.GetEntityId());
#else
                int expectedInstanceId = gameObject.GetInstanceID();
#endif

                Assert.AreEqual(
                    expectedInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    responseInstanceId);
                StringAssert.IsMatch("^-?[0-9]+$", responseInstanceId);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}

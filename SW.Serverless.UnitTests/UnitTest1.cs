using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    [TestClass]
    public class UnitTest1
    {
        static TestServer server;


        [ClassInitialize]
        public static void ClassInitialize(TestContext tcontext)
        {
            server = new TestServer(WebHost.CreateDefaultBuilder()
                .UseDefaultServiceProvider((context, options) => { options.ValidateScopes = true; })
                .UseEnvironment(Environments.Development)
                .UseStartup<TestStartup>());
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            server.Dispose();
        }

        //static readonly 

        [TestMethod]
        async public Task TestParameterless()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.5", "123");

            await serverless.InvokeAsync("TestParameterless", null);

            //Assert.AreEqual("test", result);
        }

        [TestMethod]
        async public Task TestString()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.5", null);

            var result = await serverless.InvokeAsync<string>("TestString", "test1");

            Assert.AreEqual("test1", result);
        }

        [TestMethod]
        async public Task TestInt()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.5", null);

            var result = await serverless.InvokeAsync<int>("TestInt", 12);

            Assert.AreEqual(12, result);
        }

        [TestMethod]
        async public Task TestObject()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.5", null);

            var result = await serverless.InvokeAsync<RemoteBlob>("TestObject", new RemoteBlob
            {
                Location = "loc",
                MimeType = "test",
                Name = "name1",
                Size = 55
            });

            Assert.AreEqual("name1", result.Name);
        }

        [TestMethod]
        async public Task TestNull()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.5", null);

            var result = await serverless.InvokeAsync<RemoteBlob>("TestObject", null);

            Assert.IsNull(result);
        }

        [TestMethod]
        async public Task TestGetExpectedStartupValues()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.test", null);

            var result = await serverless.GetExpectedStartupValues();

            Assert.IsNotNull(result);
        }

        [TestMethod]
        async public Task TestGetExpectedStartupValues_PrivateValues()
        {
            var serverless = server.Host.Services.GetService<IServerlessService>();

            await serverless.StartAsync("unittests.adapter.test", null);

            var result = await serverless.GetExpectedStartupValues();

            Assert.IsNotNull(result);
        }
    }
}
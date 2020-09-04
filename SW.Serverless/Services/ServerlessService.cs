﻿using Amazon.S3.Model.Internal.MarshallTransformations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SW.CloudFiles;
using SW.PrimitiveTypes;
using SW.Serverless.Sdk;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless
{
    public class ServerlessService : IServerlessService, IDisposable
    {
        private static readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);
        private const string adaptersNamingPrefix = "serverless.adapters";
        private readonly ServerlessOptions serverlessOptions;
        private readonly IMemoryCache memoryCache;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<ServerlessService> logger;
        private Process process;
        private object taskCompletionSource;
        private MethodInfo trySetResultMethod;
        private MethodInfo trySetTrySetExceptionMethod;
        private Timer invocationTimeoutTimer;
        private bool processStarted;
        private ILogger adapterLogger;

        public ServerlessService(ServerlessOptions serverlessOptions, IMemoryCache memoryCache, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
        {
            this.serverlessOptions = serverlessOptions;
            this.memoryCache = memoryCache;
            this.loggerFactory = loggerFactory;

            logger = loggerFactory.CreateLogger<ServerlessService>();

            if (serverlessOptions.CloudFilesOptions == null)
            {
                serverlessOptions.CloudFilesOptions = serviceProvider.GetService<CloudFilesOptions>();
            }

            //cloudFilesOptions = serverlessOptions.CloudFilesOptions;
        }

        async public Task StartAsync(string adapterId, IDictionary<string, string> startupValues = null)
        {
            if (string.IsNullOrWhiteSpace(adapterId) || adapterId.Contains(' '))
            {
                throw new ArgumentException("Invalid name.", nameof(adapterId));
            }

            var adapterMetadata = await Install(adapterId);

            await StartAsync(adapterId, adapterMetadata, startupValues);
        }

        async public Task StartAsync(string adapterId, string adapterPath, IDictionary<string, string> startupValues = null)
        {
            if (!File.Exists(adapterPath))
                throw new FileNotFoundException(adapterPath);

            var fakeMetadata = new AdapterMetadata
            {
                LocalPath = adapterPath
            };

            await StartAsync(adapterId, fakeMetadata, startupValues);

        }

        Task StartAsync(string adapterId, AdapterMetadata adapterMetadata, IDictionary<string, string> startupValues = null)
        {
            if (processStarted)
                throw new Exception("Already started.");

            if (startupValues == null) startupValues = new Dictionary<string, string>();

            adapterLogger = loggerFactory.CreateLogger($"{adaptersNamingPrefix}.{adapterId}".ToLower());


            var startupValuesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(startupValues)));
            var serverlessOptionsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(serverlessOptions)));
            var adapterValuesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(adapterMetadata.AdapterValues)));

            process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    Arguments = $"\"{adapterMetadata.LocalPath}\" {serverlessOptionsBase64} {startupValuesBase64} {adapterValuesBase64}",
                    WorkingDirectory = Path.GetDirectoryName(adapterMetadata.LocalPath),
                    UseShellExecute = false,

                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,

                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };

            process.OutputDataReceived += OutputDataReceived;
            process.ErrorDataReceived += ErrorDataReceived;

            if (!process.Start())
                throw new Exception("Process reused!");

            processStarted = true;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return Task.CompletedTask;
        }

        public Task<IDictionary<string, StartupValue>> GetExpectedStartupValues()
        {
            return InvokeAsync<IDictionary<string, StartupValue>>(Constants.ExpectedCommand, null);
        }

        async public Task InvokeAsync(string command, object input, int commandTimeout = 0)
        {
            await InvokeAsync<NoT>(command, input, commandTimeout);
        }

        async public Task<TResult> InvokeAsync<TResult>(string command, object input, int commandTimeout = 0)
        {
            if (commandTimeout == 0) commandTimeout = serverlessOptions.CommandTimeout;

            if (string.IsNullOrWhiteSpace(command) || command.Contains(' '))
            {
                throw new ArgumentException("Invalid name.", nameof(command));
            }

            if (!processStarted || process.HasExited)
                throw new Exception("Process not started or terminated.");

            taskCompletionSource = new TaskCompletionSource<TResult>();
            trySetResultMethod = taskCompletionSource.GetType().GetMethod("TrySetResult");
            trySetTrySetExceptionMethod = taskCompletionSource.GetType().GetMethod("TrySetException", new Type[] { typeof(Exception) });

            invocationTimeoutTimer = new Timer(
                callback: InvocationTimeoutTimerCallback,
                state: null,
                dueTime: TimeSpan.FromSeconds(commandTimeout),
                period: Timeout.InfiniteTimeSpan);

            string inputString;

            if (input == null)
                inputString = Constants.NullIdentifier;
            else if (input.GetType() == typeof(string) || input.GetType().IsPrimitive)
                inputString = input.ToString();
            else
                inputString = JsonConvert.SerializeObject(input);


            await process.StandardInput.WriteLineAsync($"{Constants.Delimiter}{command}{Constants.Delimiter}{inputString}{Constants.Delimiter}".Replace("\n", Constants.NewLineIdentifier));

            return await ((TaskCompletionSource<TResult>)taskCompletionSource).Task;
        }

        void InvocationTimeoutTimerCallback(object state)
        {
            invocationTimeoutTimer.Dispose();
            trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new TimeoutException() });
        }

        void ErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
            {
                //adapterLogger.LogWarning("Null data received on error stream.");
            }
            else if (args.Data.StartsWith(Constants.LogInformationIdentifier))
            {
                adapterLogger.LogInformation(args.Data.Replace(Constants.LogInformationIdentifier, "").Replace(Constants.NewLineIdentifier, "\n"));
            }
            else if (args.Data.StartsWith(Constants.LogWarningIdentifier))
            {
                adapterLogger.LogWarning(args.Data.Replace(Constants.LogWarningIdentifier, "").Replace(Constants.NewLineIdentifier, "\n"));
            }
            else if (args.Data.StartsWith(Constants.LogErrorIdentifier))
            {
                adapterLogger.LogError(args.Data.Replace(Constants.LogErrorIdentifier, "").Replace(Constants.NewLineIdentifier, "\n"));
            }

        }

        void OutputDataReceived(object sender, DataReceivedEventArgs args)
        {
            invocationTimeoutTimer?.Dispose();

            if (args.Data == null && taskCompletionSource != null)
            {
                trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new Exception("Received null data.") });
                return;
            }

            if (args.Data.StartsWith(Constants.ErrorIdentifier) && taskCompletionSource != null)
            {
                trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new Exception(args.Data) });
                return;
            }

            var outputSegments = args.Data.Split(Constants.Delimiter);

            if (outputSegments.Length != 3 && taskCompletionSource != null)
            {
                trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new Exception("Wrong data format.") });
                return;
            }

            var outputDenormalized = outputSegments[1].Replace(Constants.NewLineIdentifier, "\n");

            var returnType = taskCompletionSource.GetType().GetGenericArguments()[0];
            object resultTyped;

            if (outputDenormalized == Constants.NullIdentifier)
                resultTyped = null;
            else if (returnType == typeof(string))
                resultTyped = outputDenormalized;
            else if (returnType.IsPrimitive)
                resultTyped = Convert.ChangeType(outputDenormalized, returnType);
            else if (returnType == typeof(NoT))
                resultTyped = new NoT();
            else
                resultTyped = JsonConvert.DeserializeObject(outputDenormalized, returnType);


            trySetResultMethod.Invoke(taskCompletionSource, new object[] { resultTyped });
        }

        async Task<AdapterMetadata> Install(string adapterId)
        {
            var adapterMetadata = await GetAdapterMetadata(adapterId);
            var adapterDiretoryPath = $"{serverlessOptions.AdapterLocalPath}/{adapterMetadata.Hash}";
            //var adapterPath = Path.GetFullPath($"{adapterDiretoryPath}/{adapterConfig.EntryAssembly}");

            await semaphoreSlim.WaitAsync();
            try
            {
                if (!Directory.Exists(adapterDiretoryPath))
                {
                    Directory.CreateDirectory(adapterDiretoryPath);
                    try
                    {
                        using var cloudFilesService = new CloudFilesService(serverlessOptions.CloudFilesOptions);
                        using var stream = await cloudFilesService.OpenReadAsync($"{serverlessOptions.AdapterRemotePath}/{adapterId}".ToLower());
                        using var archive = new ZipArchive(stream);

                        foreach (var entry in archive.Entries)
                        {
                            var path = $"{adapterDiretoryPath}/{entry.FullName.Replace("\\", "/")}";
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            entry.ExtractToFile(path);
                        }


                        //Process.Start("chmod", $"755 {adapterPath}").WaitForExit(5000);
                    }
                    catch (Exception)
                    {
                        Directory.Delete(adapterDiretoryPath, true);
                        throw;
                    }
                }
            }
            finally
            {
                semaphoreSlim.Release();
            }

            return adapterMetadata;
        }

        async Task<AdapterMetadata> GetAdapterMetadata(string adapterId)
        {
            if (memoryCache.TryGetValue($"{adaptersNamingPrefix}.{adapterId}", out AdapterMetadata adapterMetadata))
                return adapterMetadata;

            using var cloudFilesService = new CloudFilesService(serverlessOptions.CloudFilesOptions);

            var metaData = await cloudFilesService.GetMetadataAsync($"{serverlessOptions.AdapterRemotePath}/{adapterId}".ToLower());
            adapterMetadata = new AdapterMetadata
            {
                EntryAssembly = metaData["EntryAssembly"],
                Hash = metaData["Hash"],
                AdapterValues = new Dictionary<string, string>(metaData)
            };

            adapterMetadata.LocalPath = Path.GetFullPath($"{serverlessOptions.AdapterLocalPath}/{adapterMetadata.Hash}/{adapterMetadata.EntryAssembly}");


            return memoryCache.Set($"{adaptersNamingPrefix}.{adapterId}", adapterMetadata, TimeSpan.FromMinutes(serverlessOptions.AdapterMetadataCacheDuration));
        }


        public void Dispose()
        {
            try
            {
                if (processStarted)
                {

                    if (!process.HasExited)
                    {
                        process.StandardInput.WriteLine(Constants.QuitCommand);
                        process.WaitForExit(3000);
                        if (!process.HasExited) process.Kill();
                    }

                    process.Dispose();

                    invocationTimeoutTimer?.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Service did not dispose properly.");
            }

        }

        private class NoT
        {
        }

        private class AdapterMetadata
        {
            public string Hash { get; set; }
            public string EntryAssembly { get; set; }
            public string LocalPath { get; set; }
            public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();
        }
    }
}

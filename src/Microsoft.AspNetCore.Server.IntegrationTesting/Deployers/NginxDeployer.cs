﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.IntegrationTesting.Common;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.IntegrationTesting
{
    /// <summary>
    /// Deployer for Kestrel on Nginx.
    /// </summary>
    public class NginxDeployer : SelfHostDeployer
    {
        private string _configFile;
        private readonly TimeSpan _waitTime = TimeSpan.FromSeconds(30);
        private Process _nginxProcess;

        public NginxDeployer(DeploymentParameters deploymentParameters, ILogger logger)
            : base(deploymentParameters, logger)
        {
        }

        public override async Task<DeploymentResult> DeployAsync()
        {
            using (Logger.BeginScope("Deploy"))
            {
                _configFile = Path.GetTempFileName();
                var uri = string.IsNullOrEmpty(DeploymentParameters.ApplicationBaseUriHint) ?
                    TestUriHelper.BuildTestUri() :
                    new Uri(DeploymentParameters.ApplicationBaseUriHint);

                var redirectUri = TestUriHelper.BuildTestUri();

                if (DeploymentParameters.PublishApplicationBeforeDeployment)
                {
                    DotnetPublish();
                }

                var (appUri, exitToken) = await StartSelfHostAsync(redirectUri);

                SetupNginx(appUri.ToString(), uri);

                Logger.LogInformation("Application ready at URL: {appUrl}", uri);

                // Wait for App to be loaded since Nginx returns 502 instead of 503 when App isn't loaded
                // Target actual address to avoid going through Nginx proxy
                using (var httpClient = new HttpClient())
                {
                    var response = await RetryHelper.RetryRequest(() =>
                    {
                        return httpClient.GetAsync(redirectUri);
                    }, Logger, exitToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("Deploy failed");
                    }
                }

                return new DeploymentResult
                {
                    ContentRoot = DeploymentParameters.ApplicationPath,
                    DeploymentParameters = DeploymentParameters,
                    ApplicationBaseUri = uri.ToString(),
                    HostShutdownToken = exitToken
                };
            }
        }

        private void SetupNginx(string redirectUri, Uri originalUri)
        {
            using (Logger.BeginScope("SetupNginx"))
            {
                // copy nginx.conf template and replace pertinent information
                var pidFile = Path.Combine(DeploymentParameters.ApplicationPath, $"{Guid.NewGuid()}.nginx.pid");
                var errorLog = Path.Combine(DeploymentParameters.ApplicationPath, "nginx.error.log");
                var accessLog = Path.Combine(DeploymentParameters.ApplicationPath, "nginx.access.log");
                DeploymentParameters.ServerConfigTemplateContent = DeploymentParameters.ServerConfigTemplateContent
                    .Replace("[user]", Environment.GetEnvironmentVariable("LOGNAME"))
                    .Replace("[errorlog]", errorLog)
                    .Replace("[accesslog]", accessLog)
                    .Replace("[listenPort]", originalUri.Port.ToString())
                    .Replace("[redirectUri]", redirectUri)
                    .Replace("[pidFile]", pidFile);
                Logger.LogDebug("Using PID file: {pidFile}", pidFile);
                Logger.LogDebug("Using Error Log file: {errorLog}", pidFile);
                Logger.LogDebug("Using Access Log file: {accessLog}", pidFile);
                if (Logger.IsEnabled(LogLevel.Trace))
                {
                    Logger.LogTrace($"Config File Content:{Environment.NewLine}===START CONFIG==={Environment.NewLine}{{configContent}}{Environment.NewLine}===END CONFIG===", DeploymentParameters.ServerConfigTemplateContent);
                }
                File.WriteAllText(_configFile, DeploymentParameters.ServerConfigTemplateContent);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "nginx",
                    Arguments = $"-c {_configFile}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    // Trying a work around for https://github.com/aspnet/Hosting/issues/140.
                    RedirectStandardInput = true
                };

                using (var runNginx = new Process() { StartInfo = startInfo })
                {
                    runNginx.StartAndCaptureOutAndErrToLogger("nginx start", Logger);
                    runNginx.WaitForExit((int)_waitTime.TotalMilliseconds);
                    if (runNginx.ExitCode != 0)
                    {
                        throw new Exception("Failed to start nginx");
                    }

                    // Read the PID file
                    if (!File.Exists(pidFile))
                    {
                        Logger.LogWarning("Unable to find nginx PID file: {pidFile}", pidFile);
                    }
                    else
                    {
                        var pidStr = File.ReadAllText(pidFile);
                        int pid;
                        if (string.IsNullOrEmpty(pidStr))
                        {
                            Logger.LogError("Empty PID file: {pidFile}", pidFile);
                            throw new Exception("Failed to start nginx");
                        }
                        else if (!Int32.TryParse(pidStr, out pid))
                        {
                            Logger.LogError("Invalid PID: {pid}", pidStr);
                            throw new Exception("Failed to start nginx");
                        }
                        try
                        {
                            _nginxProcess = Process.GetProcessById(pid);
                        }
                        catch (ArgumentException)
                        {
                            Logger.LogError("nginx process not running: {pid}", pid);
                            throw new Exception("Failed to start nginx");
                        }

                        Logger.LogInformation("nginx process ID {pid} started", _nginxProcess.Id);
                    }
                }
            }
        }

        public override void Dispose()
        {
            using (Logger.BeginScope("Dispose"))
            {
                if (!string.IsNullOrEmpty(_configFile))
                {
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "nginx",
                            Arguments = $"-s stop -c {_configFile}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            // Trying a work around for https://github.com/aspnet/Hosting/issues/140.
                            RedirectStandardInput = true
                        };

                        using (var runNginx = new Process() { StartInfo = startInfo })
                        {
                            runNginx.StartAndCaptureOutAndErrToLogger("nginx stop", Logger);
                            runNginx.WaitForExit((int)_waitTime.TotalMilliseconds);
                            Logger.LogInformation("nginx stop command issued");
                            if (_nginxProcess.HasExited)
                            {
                                Logger.LogInformation("nginx has shut down");
                            }
                            else
                            {
                                Logger.LogError("nginx did not shut down after {timeout} seconds", _waitTime.TotalSeconds);
                                throw new Exception("nginx failed to stop");
                            }
                        }
                    }
                    finally
                    {
                        Logger.LogDebug("Deleting config file: {configFile}", _configFile);
                        File.Delete(_configFile);
                    }
                }

                base.Dispose();
            }
        }
    }
}

// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Interpreter;

namespace Microsoft.PythonTools.Project {
    static class VirtualEnv {
        private static readonly KeyValuePair<string, string>[] UnbufferedEnv = new[] { 
            new KeyValuePair<string, string>("PYTHONUNBUFFERED", "1")
        };

        /// <summary>
        /// Installs virtualenv. If pip is not installed, the returned task will
        /// succeed but error text will be passed to the redirector.
        /// </summary>
        public static Task<bool> Install(IServiceProvider provider, IPythonInterpreterFactory factory, Redirector output = null) {
            bool elevate = provider.GetPythonToolsService().GeneralOptions.ElevatePip;
            if (factory.Configuration.Version < new Version(2, 5)) {
                if (output != null) {
                    output.WriteErrorLine("Python versions earlier than 2.5 are not supported by PTVS.");
                }
                throw new OperationCanceledException();
            } else if (factory.Configuration.Version == new Version(2, 5)) {
                return Pip.Install(provider, factory, "https://go.microsoft.com/fwlink/?LinkID=317970", elevate, output);
            } else {
                return Pip.Install(provider, factory, "https://go.microsoft.com/fwlink/?LinkID=317969", elevate, output);
            }
        }

        private static async Task ContinueCreate(IServiceProvider provider, IPythonInterpreterFactory factory, string path, bool useVEnv, Redirector output) {
            path = PathUtils.TrimEndSeparator(path);
            var name = Path.GetFileName(path);
            var dir = Path.GetDirectoryName(path);

            if (output != null) {
                output.WriteLine(Strings.VirtualEnvCreating.FormatUI(path));
                if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForVirtualEnvCreate) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }

            // Ensure the target directory exists.
            Directory.CreateDirectory(dir);

            using (var proc = ProcessOutput.Run(
                factory.Configuration.InterpreterPath,
                new[] { "-m", useVEnv ? "venv" : "virtualenv", name },
                dir,
                UnbufferedEnv,
                false,
                output
            )) {
                var exitCode = await proc;

                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(Strings.VirtualEnvCreationSucceeded.FormatUI(path));
                    } else {
                        output.WriteLine(Strings.VirtualEnvCreationFailedExitCode.FormatUI(path, exitCode));
                    }
                    if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForVirtualEnvCreate) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }

                if (exitCode != 0 || !Directory.Exists(path)) {
                    throw new InvalidOperationException(Strings.VirtualEnvCreationFailed.FormatUI(path));
                }
            }
        }

        /// <summary>
        /// Creates a virtual environment. If virtualenv is not installed, the
        /// task will succeed but error text will be passed to the redirector.
        /// </summary>
        public static Task Create(IServiceProvider provider, IPythonInterpreterFactory factory, string path, Redirector output = null) {
            factory.ThrowIfNotRunnable();
            return ContinueCreate(provider, factory, path, false, output);
        }

        /// <summary>
        /// Creates a virtual environment using venv. If venv is not available,
        /// the task will succeed but error text will be passed to the
        /// redirector.
        /// </summary>
        public static Task CreateWithVEnv(IServiceProvider provider, IPythonInterpreterFactory factory, string path, Redirector output = null) {
            factory.ThrowIfNotRunnable();
            return ContinueCreate(provider, factory, path, true, output);
        }

        /// <summary>
        /// Creates a virtual environment. If virtualenv or pip are not
        /// installed then they are downloaded and installed automatically.
        /// </summary>
        public static async Task CreateAndInstallDependencies(
            IServiceProvider provider,
            IPythonInterpreterFactory factory,
            string path,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            var modules = await factory.FindModulesAsync("pip", "virtualenv", "venv");
            bool hasPip = modules.Contains("pip");
            bool hasVirtualEnv = modules.Contains("virtualenv") || modules.Contains("venv");

            if (!hasVirtualEnv) {
                if (!hasPip) {
                    bool elevate = provider.GetPythonToolsService().GeneralOptions.ElevatePip;
                    await Pip.InstallPip(provider, factory, elevate, output);
                }
                if (!await Install(provider, factory, output)) {
                    throw new InvalidOperationException(Strings.VirtualEnvCreationFailed.FormatUI(path));
                }
            }

            await ContinueCreate(provider, factory, path, false, output);
        }

        public static InterpreterConfiguration FindInterpreterConfiguration(
            string id,
            string prefixPath,
            IInterpreterRegistryService service,
            IPythonInterpreterFactory baseInterpreter = null
        ) {

            var libPath = DerivedInterpreterFactory.FindLibPath(prefixPath);

            if (baseInterpreter == null) {
                baseInterpreter = DerivedInterpreterFactory.FindBaseInterpreterFromVirtualEnv(
                    prefixPath,
                    libPath,
                    service
                );

                if (baseInterpreter == null) {
                    return null;
                }
            }

            // The interpreter name should be the same as the base interpreter.
            string interpExe = Path.GetFileName(baseInterpreter.Configuration.InterpreterPath);
            string winterpExe = Path.GetFileName(baseInterpreter.Configuration.WindowsInterpreterPath);
            var scripts = new[] { "Scripts", "bin" };
            interpExe = PathUtils.FindFile(prefixPath, interpExe, firstCheck: scripts);
            winterpExe = PathUtils.FindFile(prefixPath, winterpExe, firstCheck: scripts);
            string pathVar = baseInterpreter.Configuration.PathEnvironmentVariable;
            string description = PathUtils.GetFileOrDirectoryName(prefixPath);

            return new InterpreterConfiguration(
                id ?? baseInterpreter.Configuration.Id,
                description,
                prefixPath,
                interpExe,
                winterpExe,
                libPath,
                pathVar,
                baseInterpreter.Configuration.Architecture,
                baseInterpreter.Configuration.Version,
                InterpreterUIMode.CannotBeDefault | InterpreterUIMode.CannotBeConfigured | InterpreterUIMode.SupportsDatabase,
                baseInterpreter != null ? string.Format("({0})", baseInterpreter.Configuration.FullDescription) : ""
            );
        }

        // This helper function is not yet needed, but may be useful at some point.

        //public static string FindLibPathFromInterpreter(string interpreterPath) {
        //    using (var output = ProcessOutput.RunHiddenAndCapture(interpreterPath, "-c", "import site; print(site.__file__)")) {
        //        output.Wait();
        //        return output.StandardOutputLines
        //            .Where(PathUtils.IsValidPath)
        //            .Select(line => Path.GetDirectoryName(line))
        //            .LastOrDefault(dir => Directory.Exists(dir));
        //    }
        //}
    }
}

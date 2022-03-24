// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Microsoft.Azure.Functions.Analyzers
{
    // Only support extensions and WebJobs core.
    // Although extensions may refer to other dlls.
    public class AssemblyCache
    {
        // Map from assembly identities to full paths
        public static AssemblyCache Instance = new AssemblyCache();

        bool _registered;

        // Assembly Display Name --> Path
        Dictionary<string, string> _map = new Dictionary<string, string>();

        // Assembly Display Name --> loaded Assembly object
        Dictionary<string, Assembly> _mapRef = new Dictionary<string, Assembly>();

        const string WebJobsAssemblyName = "Microsoft.Azure.WebJobs";
        const string WebJobsHostAssemblyName = "Microsoft.Azure.WebJobs.Host";

        JobHostMetadataProvider _tooling;

        internal JobHostMetadataProvider Tooling => _tooling;
        private int _projectCount;

        // $$$ This can get invoked multiple times concurrently
        // This will get called on every compilation.
        // So return early on subsequent initializations.
        internal void Build(Compilation compilation)
        {
            Register();

            int count;
            lock (this)
            {
                // If project references have changed, then reanalyze to pick up new dependencies.
                var refs = compilation.References.OfType<PortableExecutableReference>().ToArray();
                count = refs.Length;
                if ((count == _projectCount) && (_tooling != null))
                {
                    return; // already initialized.
                }

                // Even for netStandard/.core projects, this will still be a flattened list of the full transitive closure of dependencies.
                foreach (var asm in compilation.References.OfType<PortableExecutableReference>())
                {
                    var dispName = asm.Display; // For .net core, the displayname can be the full path
                    var path = asm.FilePath;

                    _map[dispName] = path;
                }

                // Builtins
                _mapRef["mscorlib"] = typeof(object).Assembly;
                _mapRef[WebJobsAssemblyName] = typeof(Microsoft.Azure.WebJobs.FunctionNameAttribute).Assembly;
                _mapRef[WebJobsHostAssemblyName] = typeof(Microsoft.Azure.WebJobs.JobHost).Assembly;

                // JSON.Net?
            }

            // Produce tooling object
            // TODO: In old version, Initialize() looked at every dll in _map.Values and called .AddExtension on the host config
            // What's needed here? How do you go from IWebJobsStartupType to IExtensionConfigProvider to loading the extension?
            var host = new HostBuilder()
                .ConfigureWebJobs(b =>
                {
                    b.UseExternalStartup(new CompilationWebJobsStartupTypeLocator()); // TODO: Feed in Compilation? Or something else?
                })
                .Build();
            var tooling = (JobHostMetadataProvider)host.Services.GetRequiredService<IJobHostMetadataProvider>();

            lock (this)
            {
                this._projectCount = count;
                this._tooling = tooling;
            }
        }

        public void Register()
        {
            if (_registered)
            {
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            _registered = true;
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var an = new AssemblyName(args.Name);
            var context = args.RequestingAssembly;

            Assembly asm2;
            if (_mapRef.TryGetValue(an.Name, out asm2))
            {
                return asm2;
            }

            asm2 = LoadFromProjectReference(an);
            if (asm2 != null)
            {
                _mapRef[an.Name] = asm2;
            }

            return asm2;
        }

        private Assembly LoadFromProjectReference(AssemblyName an)
        {
            foreach (var kv in _map)
            {
                var path = kv.Key;
                if (path.Contains(@"\ref\")) // Skip reference assemblies.
                {
                    continue;
                }

                var filename = Path.GetFileNameWithoutExtension(path);

                // Simplifying assumption: assume dll name matches assembly name.
                // Use this as a filter to limit the number of file-touches.
                if (string.Equals(filename, an.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var an2 = AssemblyName.GetAssemblyName(path);

                    if (string.Equals(an2.FullName, an.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        var a = Assembly.LoadFrom(path);
                        return a;
                    }
                }
            }
            return null;
        }
    }

    // TODO: Move to own file
    public class CompilationWebJobsStartupTypeLocator : IWebJobsStartupTypeLocator
    {
        // TODO: This isn't going to work, but see why DefaultStartupTypeLocator doesn't work
        public Type[] GetStartupTypes()
        {
            throw new NotImplementedException();
        }
    }
}

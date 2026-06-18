// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Tools.Properties;

#if NET
using System.Runtime.Loader;
#endif

namespace Microsoft.EntityFrameworkCore.Tools;

internal class ReflectionOperationExecutor : OperationExecutorBase
{
    private object _executor;
    private Assembly _commandsAssembly;
    private const string ReportHandlerTypeName = "Microsoft.EntityFrameworkCore.Design.OperationReportHandler";
    private const string ResultHandlerTypeName = "Microsoft.EntityFrameworkCore.Design.OperationResultHandler";
    private Type _resultHandlerType;
    private string? _efcoreVersion;
#if NET
    private AssemblyLoadContext? _assemblyLoadContext;
#endif

    public ReflectionOperationExecutor(
        string assembly,
        string? startupAssembly,
        string? designAssembly,
        string? project,
        string? projectDir,
        string? dataDirectory,
        string? rootNamespace,
        string? language,
        bool nullable,
        string[] remainingArguments,
        IOperationReportHandler reportHandler)
        : base(
            assembly, startupAssembly, designAssembly, project, projectDir, rootNamespace, language, nullable, remainingArguments,
            reportHandler)
    {
        var reporter = new OperationReporter(reportHandler);
        var configurationFile = (startupAssembly ?? assembly) + ".config";
        if (File.Exists(configurationFile))
        {
            reporter.WriteVerbose(Resources.UsingConfigurationFile(configurationFile));
            AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", configurationFile);
        }

        if (dataDirectory != null)
        {
            reporter.WriteVerbose(Resources.UsingDataDir(dataDirectory));
            AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectory);
        }

#if !NET
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
#endif

#if NET
        _commandsAssembly = DesignAssemblyPath != null
            ? AssemblyLoadContext.LoadFromAssemblyPath(DesignAssemblyPath)
            : AssemblyLoadContext.LoadFromAssemblyName(new AssemblyName(DesignAssemblyName));
#else
        _commandsAssembly = DesignAssemblyPath != null
            ? Assembly.LoadFrom(DesignAssemblyPath)
            : Assembly.Load(DesignAssemblyName);
#endif
        var reportHandlerType = _commandsAssembly.GetType(ReportHandlerTypeName, throwOnError: true, ignoreCase: false)!;

        var designReportHandler = Activator.CreateInstance(
            reportHandlerType,
            (Action<string>)reportHandler.OnError,
            (Action<string>)reportHandler.OnWarning,
            (Action<string>)reportHandler.OnInformation,
            (Action<string>)reportHandler.OnVerbose)!;

        _executor = Activator.CreateInstance(
            _commandsAssembly.GetType(ExecutorTypeName, throwOnError: true, ignoreCase: false)!,
            designReportHandler,
            new Dictionary<string, object?>
            {
                { "targetName", AssemblyFileName },
                { "startupTargetName", StartupAssemblyFileName },
                { "project", Project },
                { "projectDir", ProjectDirectory },
                { "rootNamespace", RootNamespace },
                { "language", Language },
                { "nullable", Nullable },
                { "toolsVersion", ProductInfo.GetVersion() },
                { "remainingArguments", RemainingArguments }
            })!;

        _resultHandlerType = _commandsAssembly.GetType(ResultHandlerTypeName, throwOnError: true, ignoreCase: false)!;
    }

#if NET
    protected AssemblyLoadContext AssemblyLoadContext
    {
        get
        {
            if (_assemblyLoadContext != null)
            {
                return _assemblyLoadContext;
            }

            // Load the target and startup assemblies into a collectible context so they can be
            // unloaded when this executor is disposed. The tool loads these assemblies to discover
            // the DbContext, then shells out to 'dotnet publish', which rebuilds and overwrites the
            // same bin\...\*.dll files. On Windows a loaded assembly file is locked, so the copy
            // fails unless the file has been released first. See https://github.com/dotnet/efcore/issues/25555.
            var assemblyLoadContext = new AssemblyLoadContext("EntityFrameworkCore.Tools", isCollectible: true);
            assemblyLoadContext.Resolving += (context, name) =>
            {
                var assemblyPath = Path.Combine(AppBasePath, name.Name + ".dll");
                return File.Exists(assemblyPath) ? context.LoadFromAssemblyPath(assemblyPath) : null;
            };
            _assemblyLoadContext = assemblyLoadContext;

            return assemblyLoadContext;
        }
    }
#endif

    public override string? EFCoreVersion
    {
        get
        {
            if (_efcoreVersion != null)
            {
                return _efcoreVersion;
            }

            Assembly? assembly = null;
#if NET
            assembly = DesignAssemblyPath != null
                ? AssemblyLoadContext.LoadFromAssemblyPath(DesignAssemblyPath)
                : AssemblyLoadContext.LoadFromAssemblyName(new AssemblyName(DesignAssemblyName));
#else
            assembly = DesignAssemblyPath != null
                ? Assembly.LoadFrom(DesignAssemblyPath)
                : Assembly.Load(DesignAssemblyName);
#endif
            _efcoreVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return _efcoreVersion;
        }
    }

    protected override object CreateResultHandler()
        => Activator.CreateInstance(_resultHandlerType)!;

    protected override void Execute(string operationName, object resultHandler, IDictionary arguments)
        => Activator.CreateInstance(
            _commandsAssembly.GetType(ExecutorTypeName + "+" + operationName, throwOnError: true, ignoreCase: true)!,
            _executor,
            resultHandler,
            arguments);

#if !NET
    private Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);

        foreach (var extension in new[] { ".dll", ".exe" })
        {
            var path = Path.Combine(AppBasePath, assemblyName.Name + extension);
            if (File.Exists(path))
            {
                try
                {
                    return Assembly.LoadFrom(path);
                }
                catch
                {
                }
            }
        }

        return null;
    }
#endif

    public override void Dispose()
    {
#if NET
        // Drop every reference into the collectible context and unload it so the target and startup
        // assemblies are released before 'dotnet publish' tries to overwrite their bin\...\*.dll
        // files. See https://github.com/dotnet/efcore/issues/25555.
        _executor = null!;
        _resultHandlerType = null!;
        _commandsAssembly = null!;

        if (_assemblyLoadContext is { IsCollectible: true } assemblyLoadContext)
        {
            _assemblyLoadContext = null;

            // The unload only completes once the GC observes that nothing references the context;
            // force it here so the file lock is released by the time the caller starts publishing.
            var weakReference = new WeakReference(assemblyLoadContext);
            assemblyLoadContext.Unload();
            assemblyLoadContext = null;

            for (var i = 0; weakReference.IsAlive && i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
#else
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
#endif
    }
}

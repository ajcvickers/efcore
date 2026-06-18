// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias eftool;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Microsoft.EntityFrameworkCore.Tools;

public class ReflectionOperationExecutorTest
{
    // Regression test for https://github.com/dotnet/efcore/issues/25555.
    // The bundle command loads the user's assemblies into the tool process to discover the
    // DbContext, then shells out `dotnet publish` which rebuilds and overwrites those same
    // bin\...\*.dll files. On Windows a loaded assembly file is locked, so the copy fails.
    // The executor must therefore release the user assemblies (unload its load context) when
    // disposed, before publish runs. We can't observe the Windows lock cross-platform, but we
    // can observe the portable substrate: after disposal the target assembly is no longer
    // loaded in the process.
    [Fact]
    public void Dispose_unloads_the_target_assembly()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(targetDir);
        try
        {
            var build = new BuildSource { TargetDir = targetDir, Sources = { ["Nothing.cs"] = "public class Nothing { }" } };
            var targetPath = build.Build().TargetPath;
            var designPath = Assembly.Load(new AssemblyName(OperationExecutorBase.DesignAssemblyName)).Location;

            RunOperation(targetPath, designPath);

            for (var i = 0; i < 10 && IsLoaded(targetPath); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(IsLoaded(targetPath), "Target assembly was still loaded after the executor was disposed.");
        }
        finally
        {
            Directory.Delete(targetDir, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOperation(string targetPath, string designPath)
    {
        using var executor = new ReflectionOperationExecutor(
            assembly: targetPath,
            startupAssembly: targetPath,
            designAssembly: designPath,
            project: null,
            projectDir: null,
            dataDirectory: null,
            rootNamespace: null,
            language: null,
            nullable: false,
            remainingArguments: [],
            reportHandler: new eftool::Microsoft.EntityFrameworkCore.Design.OperationReportHandler());

        // Mirrors what the bundle command does: an operation that loads the target assembly and
        // marshals its result back across the load-context boundary (the dynamic result handler).
        _ = executor.GetContextTypes().ToList();

        Assert.True(IsLoaded(targetPath), "Target assembly was not loaded by the operation; test would be vacuous.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsLoaded(string targetPath)
        => AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .Any(a => !a.IsDynamic
                && !string.IsNullOrEmpty(a.Location)
                && string.Equals(a.Location, targetPath, StringComparison.OrdinalIgnoreCase));
}

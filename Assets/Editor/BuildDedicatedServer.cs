using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildDedicatedServer
{
    private static readonly string[] ClientScenes =
    {
        "Assets/Scenes/MainMenu.unity",
        "Assets/Scenes/MainGame.unity"
    };

    private const string ServerScene = "Assets/Scenes/ServerScene.unity";
    private const string DefaultOutputPath = "glideServer.x86_64";
    private const string ClientProductName = "GlideGame";
    private const string DefaultClientOutputPath = "ClientBuild/GlideGame.exe";

    [MenuItem("Build/Glide/Build Dedicated Server")]
    public static void PerformBuild()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });

        string outputPath = GetCommandLineValue("-serverOutput", DefaultOutputPath);
        CleanBuildOutput(outputPath);

        BuildPlayerOptions options = new()
        {
            scenes = new[] { ServerScene },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None
        };

        BuildAndThrowOnFailure(options, "Dedicated server");
    }

    [MenuItem("Build/Glide/Build Windows Client")]
    public static void PerformClientBuild()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        PlayerSettings.productName = ClientProductName;

        string outputPath = GetCommandLineValue("-clientOutput", DefaultClientOutputPath);
        CleanBuildDirectory(outputPath);
        EditorUserBuildSettings.SetBuildLocation(BuildTarget.StandaloneWindows64, outputPath);

        BuildPlayerOptions options = new()
        {
            scenes = ClientScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None
        };

        BuildAndThrowOnFailure(options, "Client");
    }

    private static void BuildAndThrowOnFailure(BuildPlayerOptions options, string buildName)
    {
        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception($"{buildName} build failed: {report.summary.result}");
        }

        Debug.Log($"{buildName} build completed: {options.locationPathName}");
    }

    private static void CleanBuildOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath);
        string outputName = Path.GetFileNameWithoutExtension(fullOutputPath);

        if (File.Exists(fullOutputPath))
        {
            File.Delete(fullOutputPath);
        }

        DeleteDirectoryIfExists(Path.Combine(outputDirectory, $"{outputName}_Data"));
        DeleteDirectoryIfExists(Path.Combine(outputDirectory, $"{outputName}_BurstDebugInformation_DoNotShip"));
    }

    private static void CleanBuildDirectory(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath);

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        string projectDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string relativeOutputDirectory = Path.GetRelativePath(projectDirectory, fullOutputDirectory);

        if (relativeOutputDirectory.StartsWith("..") || Path.IsPathRooted(relativeOutputDirectory))
        {
            throw new InvalidOperationException($"Refusing to delete build directory outside project: {fullOutputDirectory}");
        }

        DeleteDirectoryIfExists(fullOutputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
    }

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static string GetCommandLineValue(string argumentName, string fallbackValue)
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == argumentName && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return fallbackValue;
    }
}

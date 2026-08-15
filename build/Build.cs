using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;

class Build : NukeBuild
{
	public static int Main () => Execute<Build>(x => x.Compile);

    public const string MLVersionName = "v0.7.3";
    private const string ProjectName = "BepInEx.MelonLoader.Loader";

    private AbsolutePath OutputDir => RootDirectory / "Output";
    private AbsolutePath MelonloaderFilesPath => OutputDir / "MelonLoader";

    Target DownloadDependencies => _ => _
	    .After(Clean)
	    .Executes(async () =>
	    {
		    using var httpClient = new HttpClient();

		    var zipPath = OutputDir / "MelonLoader.x64.zip";

		    await using var fileStream = new FileStream(zipPath, FileMode.Create);

		    await using var downloadStream =
			    await httpClient.GetStreamAsync(
				    "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip");

		    await downloadStream.CopyToAsync(fileStream);
			fileStream.Close();

			MelonloaderFilesPath.CreateOrCleanDirectory();
		    ZipFile.ExtractToDirectory(zipPath, MelonloaderFilesPath);
		    zipPath.DeleteFile();
	    });

    Target Clean => _ => _
	    .Executes(() =>
	    {
		    DotNetTasks.DotNetClean(x =>
			    x.SetProject(RootDirectory / $"{ProjectName}.UnityMono" / $"{ProjectName}.UnityMono.csproj"));

		    DotNetTasks.DotNetClean(x =>
			    x.SetProject(RootDirectory / $"{ProjectName}.IL2CPP" / $"{ProjectName}.IL2CPP.csproj"));

		    OutputDir.CreateOrCleanDirectory();
	    });

    private void HandleBuild(string projectSubname, string framework, string configuration, bool il2cpp)
    {
	    var stagingDirectory = OutputDir / "staging";
	    var stagingBepInExPath = stagingDirectory / "BepInEx" / "plugins"  / ProjectName;
	    var stagingPatchersPath = stagingDirectory / "BepInEx" / "patchers";
	    var stagingMLPath = stagingDirectory / "MLLoader";

	    stagingBepInExPath.CreateOrCleanDirectory();
	    stagingPatchersPath.CreateOrCleanDirectory();
	    stagingMLPath.CreateOrCleanDirectory();

	    (stagingMLPath / "MelonLoader").CreateDirectory();
	    (stagingMLPath / "Mods").CreateDirectory();
	    (stagingMLPath / "Plugins").CreateDirectory();
	    (stagingMLPath / "UserData").CreateDirectory();
	    (stagingMLPath / "UserLibs").CreateDirectory();

	    DotNetTasks.DotNetBuild(x =>
		    x.SetProjectFile(RootDirectory / $"{ProjectName}.{projectSubname}" / $"{ProjectName}.{projectSubname}.csproj")
			    .SetFramework(framework)
			    .SetConfiguration(configuration));

	    CopyDirectoryRecursively(
		    RootDirectory / $"{ProjectName}.{projectSubname}" / "Output" / configuration / projectSubname,
		    stagingBepInExPath,
		    DirectoryExistsPolicy.Merge);

	    stagingBepInExPath.GlobFiles("*.pdb", "*Harmony.dll").DeleteFiles();

	    // Deploy the preloader patcher that maintains the Il2Cpp.* interop aliases BEFORE
	    // BepInEx loads/memory-maps the interop assemblies (a plugin can never rewrite them).
    // It carries dnlib.dll alongside it. The patcher builds with the standard Release
    // configuration (it does not participate in the solution's BepInEx6 config mapping).
    var patcherOutput = RootDirectory / $"{ProjectName}.Patcher" / "bin" / "Release" / "net6.0";

	    var stagingMLDependencies = stagingMLPath / "MelonLoader" / "Dependencies";

		CopyDirectoryRecursively(MelonloaderFilesPath / "MelonLoader" / "Dependencies",
			stagingMLDependencies,
			DirectoryExistsPolicy.Merge);

		if (!il2cpp)
		{
			(stagingMLDependencies / "Il2CppAssemblyGenerator").DeleteDirectory();
			(stagingMLDependencies / "SupportModules" / "Il2Cpp.dll").DeleteFile();
			(stagingMLDependencies / "SupportModules" / "Il2Cpp.deps.json").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "Stress_Level_Zero_Il2Cpp.dll").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "Stress_Level_Zero_Il2Cpp.deps.json").DeleteFile();
		}
		else
		{
			(stagingMLDependencies / "SupportModules" / "Mono.dll").DeleteFile();
			(stagingMLDependencies / "SupportModules" / "Mono.pdb").DeleteFile();
			(stagingMLDependencies / "SupportModules" / "Mono.dll.mdb").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "IPA.dll").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "IPA.pdb").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "IPA.dll.mdb").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "Muse_Dash_Mono.dll").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "Muse_Dash_Mono.pdb").DeleteFile();
			(stagingMLDependencies / "CompatibilityLayers" / "Muse_Dash_Mono.dll.mdb").DeleteFile();
		}

		stagingDirectory.ZipTo(OutputDir / $"MLLoader-{projectSubname}-{configuration}-{MLVersionName}.zip");
		stagingDirectory.DeleteDirectory();
    }

    Target Compile => _ => _
	    .DependsOn(DownloadDependencies, Clean)
        .Executes(() =>
	    {
			DotNetTasks.DotNetBuild(x =>
				x.SetProjectFile(RootDirectory / $"{ProjectName}.Patcher" / $"{ProjectName}.Patcher.csproj")
					.SetFramework("net6.0")
					.SetConfiguration("Release"));

			HandleBuild("UnityMono", "net35", "BepInEx6", false);
			HandleBuild("IL2CPP", "net6.0", "BepInEx6", true);

			MelonloaderFilesPath.DeleteDirectory();
	    });
}
// msfastbuild.cs - Generates and executes a bff file for fastbuild from a .sln or .vcxproj.
// Copyright 2016 Liam Flookes and Yassine Riahi
// Available under an MIT license. See license file on github for details.
using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using CommandLine.Text;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities;
using Microsoft.Build.Locator;

namespace msfastbuild
{
	public class Options
	{
		[Option('p', "vcproject", DefaultValue = "",
		HelpText = "Path of vcproj to be built. Or name of vcproj if a solution is provided.")]
		public string Project { get; set; }

		[Option('s', "sln", DefaultValue = "",
		HelpText = "sln which contains the vcproj.")]
		public string Solution { get; set; }

		[Option('c', "config", DefaultValue = "Debug",
		HelpText = "Configuration to build.")]
		public string Config { get; set; }

		[Option('f', "platform", DefaultValue = "Win32",
		HelpText = "Platform to build.")]
		public string Platform { get; set; }

		[Option('a', "fbargs", DefaultValue = "-dist",
		HelpText = "Arguments to pass through to FASTBuild.")]
		public string FBArgs { get; set; }

		[Option('g', "generateonly", DefaultValue = false,
		HelpText = "Generate the bff file without calling FASTBuild.")]
		public bool GenerateOnly { get; set; }

		[Option('r', "regen", DefaultValue = false,
		HelpText = "If true, regenerate the bff file even when the project hasn't changed.")]
		public bool AlwaysRegenerate { get; set; }

		[Option('b', "fbpath", DefaultValue = @"FBuild.exe",
		HelpText = "Path to FASTBuild executable.")]
		public string FBPath { get; set; }

		[Option('u', "unity", DefaultValue = false,
		HelpText = "Whether to combine files into a unity step. May substantially improve compilation time, but not all projects are suitable.")]
		public bool UseUnity { get; set; }

		[Option('o', "onebff", DefaultValue = "",
		HelpText = "Write every project into this single bff, so FASTBuild schedules the whole solution at once instead of one project after another.")]
		public string OneBff { get; set; }

		[Option('z', "z7", DefaultValue = false,
		HelpText = "Compile with /Z7 where the project asks for /Zi or /ZI, so FASTBuild can cache the objects.")]
		public bool Z7 { get; set; }

		[Option('v', "vs", DefaultValue = "",
		HelpText = "Visual Studio major version to use (e.g. 18). Defaults to the newest installed.")]
		public string VSVersion { get; set; }

		[HelpOption]
		public string GetUsage()
		{
			return HelpText.AutoBuild(this,(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}

	public static class Program
	{
		static int Main(string[] args)
		{
			Options Opts = new Options();
			Parser parser = new Parser();
			if (!parser.ParseArguments(args, Opts))
			{
				Console.WriteLine(Opts.GetUsage());
				return 1;
			}

			var instances = MSBuildLocator.QueryVisualStudioInstances(VisualStudioInstanceQueryOptions.Default)
				.OrderByDescending(i => i.Version).ToList();
			if (!string.IsNullOrEmpty(Opts.VSVersion))
				instances = instances.Where(i => i.Version.Major.ToString() == Opts.VSVersion).ToList();
			if (!instances.Any())
			{
				Console.WriteLine("No Visual Studio installation found.");
				return 1;
			}
			Console.WriteLine("Using " + instances[0].Name + " " + instances[0].Version + " at " + instances[0].VisualStudioRootPath);
			MSBuildLocator.RegisterInstance(instances[0]);
			return Start(Opts);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Start(Options Opts)
		{
			msfastbuild.CommandLineOptions = Opts;
			return msfastbuild.Run();
		}
	}

	public class msfastbuild
	{
		static public string BFFOutputFilePath = "fbuild.bff";
		static public Options CommandLineOptions = new Options();
		static public MSFBProject CurrentProject;
		static public Assembly CPPTasksAssembly;
		static public string PreBuildBatchFile = "";
		static public string PostBuildBatchFile = "";
		static public string SolutionDir = "";
		static public string SolutionPath = "";
		static public bool HasCompileActions = true;
		static public string VcvarsAll = "";
		static public string VcvarsArch = "x64";

		// Solution configuration mapping: project full path -> project configuration/platform.
		static Dictionary<string, ProjectConfigurationInSolution> SolutionConfigMap =
			new Dictionary<string, ProjectConfigurationInSolution>(StringComparer.OrdinalIgnoreCase);

		public enum BuildType
		{
			Application,
			StaticLib,
			DynamicLib
		}

		static public BuildType BuildOutput = BuildType.Application;

		public class MSFBProject
		{
			public Project Proj;
			public List<MSFBProject> Dependents = new List<MSFBProject>();
			public List<MSFBProject> Dependencies = new List<MSFBProject>();
			public string AdditionalLinkInputs = "";
			public string NodePrefix = "";
			public string OutputNode = "";
		}

		// Called only after MSBuildLocator has registered: this class's static fields hold MSBuild types.
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static int Run()
		{
			if (string.IsNullOrEmpty(CommandLineOptions.Solution) && string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				Console.WriteLine("No vcxproj or sln provided: nothing to do!");
				Console.WriteLine(CommandLineOptions.GetUsage());
				return 1;
			}

			List <string> ProjectsToBuild = new List<string>();
			if (!string.IsNullOrEmpty(CommandLineOptions.Solution) && File.Exists(CommandLineOptions.Solution))
			{
				try
				{
					SolutionPath = Path.GetFullPath(CommandLineOptions.Solution);
					SolutionFile Sln = SolutionFile.Parse(SolutionPath);
					string SolutionConfig = CommandLineOptions.Config + "|" + CommandLineOptions.Platform;
					List<ProjectInSolution> SolutionProjects = Sln.ProjectsInOrder.Where(el => el.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).ToList();
					foreach (var p in SolutionProjects)
					{
						ProjectConfigurationInSolution pc;
						if (p.ProjectConfigurations.TryGetValue(SolutionConfig, out pc))
							SolutionConfigMap[Path.GetFullPath(p.AbsolutePath)] = pc;
					}
					if (string.IsNullOrEmpty(CommandLineOptions.Project))
					{
						ProjectsToBuild = SolutionProjects
							.Where(p => !SolutionConfigMap.ContainsKey(Path.GetFullPath(p.AbsolutePath)) || SolutionConfigMap[Path.GetFullPath(p.AbsolutePath)].IncludeInBuild)
							.Select(el => el.AbsolutePath).ToList();
					}
					else
					{
						ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
					}

					SolutionDir = Path.GetDirectoryName(SolutionPath);
					SolutionDir = SolutionDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					if(SolutionDir.Last() != Path.AltDirectorySeparatorChar)
						SolutionDir += Path.AltDirectorySeparatorChar;
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to load input file: " + CommandLineOptions.Solution + ", exception thrown was: " + e.Message);
					return 1;
				}
			}
			else if (!string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
			}

			List<MSFBProject> EvaluatedProjects = new List<MSFBProject>();

			for (int i=0; i < ProjectsToBuild.Count; ++i)
			{
				EvaluateProjectReferences(ProjectsToBuild[i], EvaluatedProjects, null);
			}

			bool OneBff = !string.IsNullOrEmpty(CommandLineOptions.OneBff);
			for (int i = 0; i < EvaluatedProjects.Count; ++i)
			{
				EvaluatedProjects[i].NodePrefix = OneBff ? string.Format("p{0}_", i) : "";
				foreach (var dep in EvaluatedProjects[i].Dependents)
					if (!dep.Dependencies.Contains(EvaluatedProjects[i]))
						dep.Dependencies.Add(EvaluatedProjects[i]);
			}

			StringBuilder Combined = new StringBuilder();
			List<string> AllOutputs = new List<string>();
			CompilerNodes.Clear();

			int ProjectsBuilt = 0;
			int Failures = 0;
			foreach(MSFBProject project in EvaluatedProjects)
			{
				CurrentProject = project;
				string VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPath");
				if(string.IsNullOrEmpty(VCTargetsPath))
				{
					VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPathActual");
				}
				if(string.IsNullOrEmpty(VCTargetsPath))
				{
					Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + Path.GetFileName(CurrentProject.Proj.FullPath) + ". Is this a supported version of Visual Studio?");
					Failures++;
					continue;
				}
				string BuildDllPath = Path.Combine(VCTargetsPath, "Microsoft.Build.CPPTasks.Common.dll");
				if(!File.Exists(BuildDllPath))
				{
					Console.WriteLine("Failed to find " + BuildDllPath + ". Is this a supported version of Visual Studio?");
					Failures++;
					continue;
				}

				CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);
				string Config = CurrentProject.Proj.GetPropertyValue("Configuration");
				string Platform = CurrentProject.Proj.GetPropertyValue("Platform");
				BFFOutputFilePath = Path.GetDirectoryName(CurrentProject.Proj.FullPath) + "\\" + Path.GetFileName(CurrentProject.Proj.FullPath) + "_" + Config.Replace(" ", "") + "_" + Platform.Replace(" ", "") + ".bff";
				string Bff;
				try
				{
					Bff = GenerateBffFromVcxproj(Config, Platform, OneBff);
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to generate " + Path.GetFileName(CurrentProject.Proj.FullPath) + ": " + e);
					Failures++;
					continue;
				}

				if (OneBff)
				{
					Combined.Append(Bff);
					if (!string.IsNullOrEmpty(CurrentProject.OutputNode))
						AllOutputs.Add(CurrentProject.OutputNode);
					continue;
				}

				if (!CommandLineOptions.GenerateOnly)
				{
					if (HasCompileActions && !ExecuteBffFile(CurrentProject.Proj.FullPath, BFFOutputFilePath))
						break;
					else
						ProjectsBuilt++;
				}
			}

			if (OneBff)
			{
				BFFOutputFilePath = Path.GetFullPath(CommandLineOptions.OneBff);
				StringBuilder Header = new StringBuilder();
				Header.Append(SettingsBlock(EvaluatedProjects.First().Proj));
				foreach (var c in CompilerNodes)
					Header.Append(c.Value);
				Header.Append(MkdirsNode(BFFOutputFilePath));
				Combined.Insert(0, Header.ToString());
				Combined.AppendFormat("Alias('all')\n{{\n\t.Targets = {{ {0} }}\n}}\n", string.Join(",", AllOutputs.Select(o => "'" + o + "'")));
				File.WriteAllText(BFFOutputFilePath, Combined.ToString());
				Console.WriteLine("Wrote " + BFFOutputFilePath + " for " + (EvaluatedProjects.Count - Failures) + "/" + EvaluatedProjects.Count + " projects.");
				if (!CommandLineOptions.GenerateOnly && Failures == 0)
					return ExecuteBffFile(BFFOutputFilePath, BFFOutputFilePath) ? 0 : 1;
				return Failures == 0 ? 0 : 1;
			}

			Console.WriteLine(ProjectsBuilt + "/" + EvaluatedProjects.Count + " built.");
			return Failures == 0 ? 0 : 1;
		}

		static public void EvaluateProjectReferences(string ProjectPath, List<MSFBProject> evaluatedProjects, MSFBProject dependent)
		{
			if (!string.IsNullOrEmpty(ProjectPath) && File.Exists(ProjectPath))
			{
				try
				{
					string FullPath = Path.GetFullPath(ProjectPath);
					MSFBProject newProj = evaluatedProjects.Find(elem => string.Equals(elem.Proj.FullPath, FullPath, StringComparison.OrdinalIgnoreCase));
					if (newProj != null)
					{
						if (dependent != null)
							newProj.Dependents.Add(dependent);
					}
					else
					{
						var Globals = new Dictionary<string, string>();
						ProjectConfigurationInSolution pc;
						if (SolutionConfigMap.TryGetValue(FullPath, out pc))
						{
							Globals["Configuration"] = pc.ConfigurationName;
							Globals["Platform"] = pc.PlatformName;
						}
						else
						{
							Globals["Configuration"] = CommandLineOptions.Config;
							Globals["Platform"] = CommandLineOptions.Platform;
						}
						if (!string.IsNullOrEmpty(SolutionDir))
						{
							Globals["SolutionDir"] = SolutionDir.Replace('/', '\\');
							Globals["SolutionPath"] = SolutionPath;
							Globals["SolutionFileName"] = Path.GetFileName(SolutionPath);
							Globals["SolutionName"] = Path.GetFileNameWithoutExtension(SolutionPath);
							Globals["SolutionExt"] = Path.GetExtension(SolutionPath);
						}
						ProjectCollection projColl = new ProjectCollection(Globals);
						newProj = new MSFBProject();
						Project proj = projColl.LoadProject(FullPath);

						if (proj != null)
						{
							newProj.Proj = proj;
							if (dependent != null)
							{
								newProj.Dependents.Add(dependent);
							}
							var ProjectReferences = proj.Items.Where(elem => elem.ItemType == "ProjectReference");
							foreach (var ProjRef in ProjectReferences)
							{
								if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
								{
									EvaluateProjectReferences(Path.Combine(Path.GetDirectoryName(proj.FullPath), ProjRef.EvaluatedInclude), evaluatedProjects, newProj);
								}
							}
							evaluatedProjects.Add(newProj);
						}
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Exception loading project " + ProjectPath + " exception " + e.Message);
					return;
				}
			}
		}

		static public bool ExecuteBffFile(string ProjectPath, string BffPath)
		{
			string projectDir = Path.GetDirectoryName(ProjectPath) + "\\";

			string BatchFileText = "@echo off\n"
				+ "%comspec% /c \"\"" + VcvarsAll + "\" " + VcvarsArch
				+ " && \"" + CommandLineOptions.FBPath  +"\" %*\"";
			File.WriteAllText(projectDir + "fb.bat", BatchFileText);

			Console.WriteLine("Building " + Path.GetFileNameWithoutExtension(ProjectPath));

			try
			{
				System.Diagnostics.Process FBProcess = new System.Diagnostics.Process();
				FBProcess.StartInfo.FileName = projectDir + "fb.bat";
				FBProcess.StartInfo.Arguments = "-config \"" + BffPath + "\" " + CommandLineOptions.FBArgs;
				FBProcess.StartInfo.RedirectStandardOutput = true;
				FBProcess.StartInfo.UseShellExecute = false;
				FBProcess.StartInfo.WorkingDirectory = projectDir;
				FBProcess.StartInfo.StandardOutputEncoding = Console.OutputEncoding;

				FBProcess.Start();
				while (!FBProcess.StandardOutput.EndOfStream)
				{
					Console.Write(FBProcess.StandardOutput.ReadLine() + "\n");
				}
				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Problem launching fb.bat. Exception: " + e.Message);
				return false;
			}
		}

		// FASTBuild string escaping: ^ escapes ' " $ and itself.
		static string Esc(string s)
		{
			return s.Replace("^", "^^").Replace("'", "^'").Replace("$", "^$");
		}

		static string Fwd(string s)
		{
			return s.Replace('\\', '/');
		}

		public class ObjectListNode
		{
			string Compiler;
			string CompilerOutputPath;
			string CompilerOutputPrefix;
			string CompilerOptions;
			string CompilerOutputExtension;
			string PrecompiledHeaderString;

			List<string> CompilerInputFiles;

			public ObjectListNode(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOutputPrefix, string InCompilerOptions, string InPrecompiledHeaderString, string InCompilerOutputExtension = "")
			{
				CompilerInputFiles = new List<string>();
				CompilerInputFiles.Add(InputFile);
				Compiler = InCompiler;
				CompilerOutputPath = InCompilerOutputPath;
				CompilerOutputPrefix = InCompilerOutputPrefix;
				CompilerOptions = InCompilerOptions;
				CompilerOutputExtension = InCompilerOutputExtension;
				PrecompiledHeaderString = InPrecompiledHeaderString;
			}

			public bool AddIfMatches(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOutputPrefix, string InCompilerOptions, string InPrecompiledHeaderString)
			{
				if(Compiler == InCompiler && CompilerOutputPath == InCompilerOutputPath && CompilerOutputPrefix == InCompilerOutputPrefix && CompilerOptions == InCompilerOptions && PrecompiledHeaderString == InPrecompiledHeaderString)
				{
					CompilerInputFiles.Add(InputFile);
					return true;
				}
				return false;
			}

			public string ToString(string NodeName)
			{
				bool UsedUnity = false;
				string ResultString = "";
				if(CommandLineOptions.UseUnity && !Compiler.StartsWith("rc") && CompilerInputFiles.Count > 1)
				{
					StringBuilder UnityListString = new StringBuilder(string.Format("Unity('unity_{0}')\n{{\n", NodeName));
					UnityListString.AppendFormat("\t.UnityInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", Esc(el))).ToArray()));
					UnityListString.AppendFormat("\t.UnityOutputPath = '{0}'\n", Esc(CompilerOutputPath));
					UnityListString.AppendFormat("\t.UnityNumFiles = {0}\n", 1 + CompilerInputFiles.Count/10);
					UnityListString.Append("}\n\n");
					UsedUnity = true;
					ResultString = UnityListString.ToString();
				}

				StringBuilder ObjectListString = new StringBuilder(string.Format("ObjectList('{0}')\n{{\n", NodeName));
				ObjectListString.AppendFormat("\t.Compiler = '{0}'\n", Compiler);
				ObjectListString.AppendFormat("\t.CompilerOutputPath = '{0}'\n", Esc(CompilerOutputPath));
				if (!string.IsNullOrEmpty(CompilerOutputPrefix))
				{
					ObjectListString.AppendFormat("\t.CompilerOutputPrefix = '{0}'\n", Esc(CompilerOutputPrefix));
				}
				if(UsedUnity)
				{
					ObjectListString.AppendFormat("\t.CompilerInputUnity = {{ {0} }}\n", string.Format("'unity_{0}'", NodeName));
				}
				else
				{
					ObjectListString.AppendFormat("\t.CompilerInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", Esc(el))).ToArray()));
				}
				ObjectListString.AppendFormat("\t.CompilerOptions = '{0}'\n", Esc(CompilerOptions));
				if (!string.IsNullOrEmpty(CompilerOutputExtension))
				{
					ObjectListString.AppendFormat("\t.CompilerOutputExtension = '{0}'\n", CompilerOutputExtension);
				}
				if (!string.IsNullOrEmpty(PrecompiledHeaderString))
				{
					ObjectListString.Append(PrecompiledHeaderString);
				}
				// cl.exe does not create the directory of /Fd, so every compile waits for mkdirs.
				if (!string.IsNullOrEmpty(PreBuildBatchFile))
				{
					ObjectListString.AppendFormat("\t.PreBuildDependencies = {{ 'mkdirs', '{0}prebuild' }}\n", CurrentProject.NodePrefix);
				}
				else
				{
					ObjectListString.Append("\t.PreBuildDependencies = { 'mkdirs' }\n");
				}
				ObjectListString.Append("}\n\n");
				ResultString += ObjectListString.ToString();
				return ResultString;
			}
		}

		// Compiler() declarations, keyed by executable path, shared by every project of a run.
		static SortedDictionary<string, string> CompilerNodes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		static Dictionary<string, string> CompilerNodeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// Finds an executable on the project's evaluated ExecutablePath, which is the search
		// path the MSBuild ToolTasks themselves use.
		static string FindTool(Project P, string Exe)
		{
			foreach (string Dir in P.GetPropertyValue("ExecutablePath").Split(';'))
			{
				if (string.IsNullOrWhiteSpace(Dir)) continue;
				string Candidate = Path.Combine(Dir.Trim(), Exe);
				if (File.Exists(Candidate))
					return Path.GetFullPath(Candidate);
			}
			throw new Exception("Cannot find " + Exe + " on ExecutablePath of " + P.FullPath);
		}

		// The C/C++ compiler the CL task would run: CLToolPath\CLToolExe when the toolset sets
		// them (ClangCl sets clang-cl.exe), else cl.exe on ExecutablePath.
		static string CompilerExecutable(Project P)
		{
			string ToolExe = P.GetPropertyValue("CLToolExe");
			string ToolPath = P.GetPropertyValue("CLToolPath");
			if (string.IsNullOrEmpty(ToolExe)) ToolExe = "cl.exe";
			if (!string.IsNullOrEmpty(ToolPath) && File.Exists(Path.Combine(ToolPath, ToolExe)))
				return Path.GetFullPath(Path.Combine(ToolPath, ToolExe));
			return FindTool(P, ToolExe);
		}

		static string CompilerNode(string Exe, bool IsRc, Project P)
		{
			string Name;
			if (CompilerNodeNames.TryGetValue(Exe, out Name))
				return Name;
			Name = (IsRc ? "rc_" : "cc_") + CompilerNodeNames.Count;
			CompilerNodeNames[Exe] = Name;

			StringBuilder S = new StringBuilder(string.Format("Compiler('{0}')\n{{\n", Name));
			S.AppendFormat("\t.Executable = '{0}'\n", Esc(Exe));
			if (!IsRc)
				S.Append("\t.Environment =\n\t{\n" + EnvBody(P) + "\n\t}\n");
			string FileName = Path.GetFileName(Exe).ToLowerInvariant();
			if (IsRc)
			{
				S.Append("\t.CompilerFamily = 'custom'\n");
			}
			else if (FileName == "clang-cl.exe")
			{
				S.Append("\t.CompilerFamily = 'clang-cl'\n");
			}
			else
			{
				S.Append("\t.CompilerFamily = 'msvc'\n");
				// The DLLs cl.exe loads from its own directory, plus the localized UI dll.
				// The directory also carries IDE and sanitizer DLLs the compiler never touches.
				string Dir = Path.GetDirectoryName(Exe);
				var Needed = new System.Text.RegularExpressions.Regex(@"^(c1|c1xx|c2|atlprov|tbbmalloc|vcruntime140.*|msvcp140.*|msobj140|mspdb140|mspdbcore|mspft140|mspdbst|cfgpersist|vcmeta)\.dll$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
				var Extra = Directory.GetFiles(Dir, "*.dll").Where(f => Needed.IsMatch(Path.GetFileName(f))).ToList();
				if (File.Exists(Path.Combine(Dir, "mspdbsrv.exe")))
					Extra.Add(Path.Combine(Dir, "mspdbsrv.exe"));
				string Clui = Path.Combine(Dir, "1033", "clui.dll");
				if (!File.Exists(Clui))
				{
					var Loc = Directory.GetDirectories(Dir).Where(d => Path.GetFileName(d).All(char.IsDigit) && File.Exists(Path.Combine(d, "clui.dll"))).FirstOrDefault();
					Clui = Loc != null ? Path.Combine(Loc, "clui.dll") : null;
				}
				if (Clui != null)
					Extra.Add(Clui);
				S.Append("\t.ExtraFiles =\n\t{\n");
				S.Append(string.Join(",\n", Extra.Select(f => "\t\t'" + Esc(f) + "'")));
				S.Append("\n\t}\n");
			}
			S.Append("}\n\n");
			CompilerNodes[Name] = S.ToString();
			return Name;
		}

		static string SettingsBlock(Project P)
		{
			StringBuilder S = new StringBuilder();
			S.Append("Settings\n{\n\t.Environment =\n\t{\n");
			S.Append(EnvBody(P));
			S.Append("\n\t}\n}\n\n");
			return S.ToString();
		}

		// INCLUDE/LIB/PATH come from the toolset, so a sln mixing ClangCl and MSVC projects
		// needs them per compiler: clang's headers on cl.exe's INCLUDE break it (C1021).
		static string EnvBody(Project P)
		{
			var Env = new List<string>
			{
				"INCLUDE=" + P.GetPropertyValue("IncludePath"),
				"EXTERNAL_INCLUDE=" + P.GetPropertyValue("ExternalIncludePath"),
				"LIB=" + P.GetPropertyValue("LibraryPath"),
				"LIBPATH=" + P.GetPropertyValue("ReferencePath"),
				"PATH=" + P.GetPropertyValue("ExecutablePath") + ";" + Environment.GetEnvironmentVariable("SystemRoot") + "\\System32",
				"TMP=" + Path.GetTempPath(),
				"TEMP=" + Path.GetTempPath(),
				"SystemRoot=" + Environment.GetEnvironmentVariable("SystemRoot"),
			};
			return string.Join(",\n", Env.Select(e => "\t\t'" + Esc(e) + "'"));
		}

		// Splits an ObjectFileName value into the directory FASTBuild writes into and the
		// prefix it puts before the source's base name. "$(IntDir)" -> (IntDir, "");
		// "$(IntDir)dom_%(Filename).obj" -> (IntDir, "dom_").
		static void SplitObjectFileName(string ObjectFileName, string Source, string IntDir, out string Dir, out string Prefix)
		{
			Prefix = "";
			if (string.IsNullOrEmpty(ObjectFileName))
			{
				Dir = IntDir;
				return;
			}
			if (ObjectFileName.EndsWith("\\") || ObjectFileName.EndsWith("/"))
			{
				Dir = ObjectFileName;
				return;
			}
			Dir = Path.GetDirectoryName(ObjectFileName) + "\\";
			string File_ = Path.GetFileNameWithoutExtension(ObjectFileName);
			string Base = Path.GetFileNameWithoutExtension(Source);
			if (File_.EndsWith(Base, StringComparison.OrdinalIgnoreCase))
				Prefix = File_.Substring(0, File_.Length - Base.Length);
			else
				throw new Exception("Unsupported ObjectFileName " + ObjectFileName + " for " + Source);
		}

		static string ProjectDirRooted(Project P, string PathValue)
		{
			if (Path.IsPathRooted(PathValue))
				return Path.GetFullPath(PathValue);
			return Path.GetFullPath(Path.Combine(P.DirectoryPath, PathValue));
		}

		static private string GenerateBffFromVcxproj(string Config, string Platform, bool OneBff)
		{
			Project ActiveProject = CurrentProject.Proj;
			string N = CurrentProject.NodePrefix;
			PreBuildBatchFile = "";
			PostBuildBatchFile = "";

			string configType = ActiveProject.GetPropertyValue("ConfigurationType");
			switch(configType)
			{
				case "DynamicLibrary": BuildOutput = BuildType.DynamicLib; break;
				case "StaticLibrary": BuildOutput = BuildType.StaticLib; break;
				default:
				case "Application": BuildOutput = BuildType.Application; break;
			}

			string IntDir = ProjectDirRooted(ActiveProject, ActiveProject.GetPropertyValue("IntDir"));
			if (!IntDir.EndsWith("\\")) IntDir += "\\";
			NeedDirOf(IntDir);
			NeedDirOf(ProjectDirRooted(ActiveProject, ActiveProject.GetPropertyValue("OutDir")) + "\\");

			VcvarsAll = Path.Combine(ActiveProject.GetPropertyValue("VCInstallDir"), "Auxiliary", "Build", "vcvarsall.bat");
			VcvarsArch = (Platform == "Win32" || Platform == "x86") ? "x86" : (Platform == "ARM64" ? "arm64" : "x64");

			StringBuilder OutputString = new StringBuilder();
			if (!OneBff)
				OutputString.Append(SettingsBlock(ActiveProject));

			string CC = CompilerNode(CompilerExecutable(ActiveProject), false, ActiveProject);

			if (ActiveProject.GetItems("PreBuildEvent").Any())
			{
				var buildEvent = ActiveProject.GetItems("PreBuildEvent").First();
				string Command = buildEvent.GetMetadataValue("Command");
				if(!string.IsNullOrEmpty(Command))
				{
					string BatchText = "call \"" + VcvarsAll + "\" " + VcvarsArch + "\n";
					PreBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_prebuild.bat");
					File.WriteAllText(PreBuildBatchFile, BatchText + Command);
					OutputString.AppendFormat("Exec('{0}prebuild') \n{{\n", N);
					OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", Esc(PreBuildBatchFile));
					OutputString.AppendFormat("\t.ExecInput = '{0}' \n", Esc(PreBuildBatchFile));
					OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", Esc(PreBuildBatchFile + ".txt"));
					OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
					OutputString.Append("}\n\n");
				}
			}

			foreach (string Unsupported in new[] { "CustomBuild", "MASM", "NASM", "MARMASM" })
			{
				if (ActiveProject.GetItems(Unsupported).Any(i => i.GetMetadataValue("ExcludedFromBuild") != "true"))
					throw new Exception(Unsupported + " items are not supported");
			}
			if (!string.IsNullOrEmpty(ActiveProject.GetPropertyValue("CustomBuildStep")) || ActiveProject.ItemDefinitions.ContainsKey("CustomBuildStep") && !string.IsNullOrEmpty(ActiveProject.ItemDefinitions["CustomBuildStep"].GetMetadataValue("Command")))
				throw new Exception("CustomBuildStep is not supported");

			string CompilerOptions = "";

			List<ObjectListNode> ObjectLists = new List<ObjectListNode>();
			var CompileItems = ActiveProject.GetItems("ClCompile");
			string PrecompiledHeaderString = "";

			// CL task properties that are build-system plumbing rather than compiler flags, or
			// that FASTBuild owns (/MP, the object file name).
			string[] ClSkip = new string[] { "ObjectFileName", "AssemblerListingLocation", "MultiProcessorCompilation", "ProcessorNumber",
				"TrackerLogDirectory", "MinimalRebuildFromTracking", "BuildingInIDE", "AcceptableNonZeroExitCodes" };

			foreach (var Item in CompileItems)
			{
				if (Item.GetMetadataValue("ExcludedFromBuild") == "true")
					continue;
				if (Item.GetMetadataValue("PrecompiledHeader") == "Create")
				{
					ToolTask CLtask = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
					CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
					string pchCompilerOptions = GenerateTaskCommandLine(CLtask, ClSkip.Concat(new string[] { "PrecompiledHeaderOutputFile" }).ToArray(), Item.Metadata) + " /FS";
					PrecompiledHeaderString = "\t.PCHOptions = '" + Esc(string.Format("\"%1\" /Fp\"%2\" /Fo\"%3\" {0} ", pchCompilerOptions)) + "'\n";
					PrecompiledHeaderString += "\t.PCHInputFile = '" + Esc(Item.EvaluatedInclude) + "'\n";
					PrecompiledHeaderString += "\t.PCHOutputFile = '" + Esc(Item.GetMetadataValue("PrecompiledHeaderOutputFile")) + "'\n";
					break; //Assumes only one pch...
				}
			}

			foreach (var Item in CompileItems)
			{
				if (Item.GetMetadataValue("ExcludedFromBuild") == "true")
					continue;
				if (Item.GetMetadataValue("PrecompiledHeader") == "Create")
					continue;
				bool ExcludePrecompiledHeader = Item.GetMetadataValue("PrecompiledHeader") == "NotUsing";

				ToolTask Task = (ToolTask) Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
				Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() }); //CPPTasks throws an exception otherwise...
				string TempCompilerOptions = GenerateTaskCommandLine(Task, ClSkip, Item.Metadata) + " /FS";
				string CompileAs = Item.GetMetadataValue("CompileAs");
				if (CompileAs != "CompileAsC" && CompileAs != "CompileAsCpp")
				{
					if (Path.GetExtension(Item.EvaluatedInclude).ToLowerInvariant() == ".c")
						TempCompilerOptions += " /TC";
					else
						TempCompilerOptions += " /TP";
				}
				CompilerOptions = TempCompilerOptions;
				string OutDirForItem, Prefix;
				SplitObjectFileName(Item.GetMetadataValue("ObjectFileName"), Item.EvaluatedInclude, IntDir, out OutDirForItem, out Prefix);
				OutDirForItem = ProjectDirRooted(ActiveProject, OutDirForItem);
				string Input = ProjectDirRooted(ActiveProject, Item.EvaluatedInclude);
				string FormattedCompilerOptions = string.Format("\"%1\" /Fo\"%2\" {0}", TempCompilerOptions);
				string Pch = ExcludePrecompiledHeader ? "" : PrecompiledHeaderString;
				if(!ObjectLists.Any(el => el.AddIfMatches(Input, CC, OutDirForItem, Prefix, FormattedCompilerOptions, Pch)))
				{
					ObjectLists.Add(new ObjectListNode(Input, CC, OutDirForItem, Prefix, FormattedCompilerOptions, Pch));
				}
			}

			PrecompiledHeaderString = "";

			var ResourceCompileItems = ActiveProject.GetItems("ResourceCompile").Where(i => i.GetMetadataValue("ExcludedFromBuild") != "true").ToList();
			if (ResourceCompileItems.Any())
			{
				string RC = CompilerNode(FindTool(ActiveProject, "rc.exe"), true, ActiveProject);
				foreach (var Item in ResourceCompileItems)
				{
					ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC"));
					string ResourceCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ResourceOutputFileName", "DesigntimePreprocessorDefinitions", "TrackerLogDirectory", "MinimalRebuildFromTracking" }, Item.Metadata);

					string formattedCompilerOptions = string.Format("{0} /fo\"%2\" \"%1\"", ResourceCompilerOptions);
					string Input = ProjectDirRooted(ActiveProject, Item.EvaluatedInclude);
					if (!ObjectLists.Any(el => el.AddIfMatches(Input, RC, IntDir, "", formattedCompilerOptions, PrecompiledHeaderString)))
					{
						ObjectLists.Add(new ObjectListNode(Input, RC, IntDir, "", formattedCompilerOptions, PrecompiledHeaderString, ".res"));
					}
				}
			}

			int ActionNumber = 0;
			foreach (ObjectListNode ObjList in ObjectLists)
			{
				OutputString.Append(ObjList.ToString(N + "action_" + ActionNumber));
				ActionNumber++;
			}

			HasCompileActions = ActionNumber > 0;
			if (!HasCompileActions)
				Console.WriteLine(Path.GetFileName(ActiveProject.FullPath) + " has no actions to compile.");

			List<string> CompileActions = Enumerable.Range(0, ActionNumber).Select(x => string.Format("'{0}action_{1}'", N, x)).ToList();
			CurrentProject.OutputNode = "";

			if (BuildOutput == BuildType.Application || BuildOutput == BuildType.DynamicLib)
			{
				CurrentProject.OutputNode = N + "output";
				OutputString.AppendFormat("{0}('{1}')\n{{\n", BuildOutput == BuildType.Application ? "Executable" : "DLL", CurrentProject.OutputNode);
				OutputString.AppendFormat("\t.Linker = '{0}'\n", Esc(FindTool(ActiveProject, "link.exe")));

				var LinkDefinitions = ActiveProject.ItemDefinitions["Link"];
				string OutputFile = ProjectDirRooted(ActiveProject, LinkDefinitions.GetMetadataValue("OutputFile"));
				NeedDirOf(OutputFile);

				if(HasCompileActions && !OneBff)
				{
					string DependencyOutputPath = Fwd(ProjectDirRooted(ActiveProject, LinkDefinitions.GetMetadataValue("ImportLibrary")));
					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
				string LinkerOptions = GenerateTaskCommandLine(Task, new string[] { "OutputFile", "ProfileGuidedDatabase", "TrackerLogDirectory", "MinimalRebuildFromTracking", "BuildingInIDE" }, LinkDefinitions.Metadata);

				if (!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					LinkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LinkerOptions = '{0}'\n", Esc("\"%1\" /OUT:\"%2\" " + LinkerOptions));
				OutputString.AppendFormat("\t.LinkerOutput = '{0}'\n", Esc(OutputFile));

				var Libraries = new List<string>(CompileActions);
				if (OneBff)
				{
					// Direct references only, as MSBuild's ResolvedLinkLib does.
					foreach (var dep in CurrentProject.Dependencies)
						if (!string.IsNullOrEmpty(dep.OutputNode) && dep.Proj.GetPropertyValue("ConfigurationType") == "StaticLibrary")
							Libraries.Add("'" + dep.OutputNode + "'");
				}
				OutputString.Append("\t.Libraries = { ");
				OutputString.Append(string.Join(",", Libraries));
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}
			else if(BuildOutput == BuildType.StaticLib && HasCompileActions)
			{
				CurrentProject.OutputNode = N + "output";
				OutputString.AppendFormat("Library('{0}')\n{{\n", CurrentProject.OutputNode);
				OutputString.AppendFormat("\t.Compiler = '{0}'\n", CC);
				OutputString.AppendFormat("\t.CompilerOptions = '{0}'\n", Esc(string.Format("\"%1\" /Fo\"%2\" /c {0}", CompilerOptions)));
				OutputString.AppendFormat("\t.CompilerOutputPath = '{0}'\n", Esc(IntDir));
				OutputString.AppendFormat("\t.Librarian = '{0}'\n", Esc(FindTool(ActiveProject, "lib.exe")));

				var LibDefinitions = ActiveProject.ItemDefinitions["Lib"];
				string OutputFile = ProjectDirRooted(ActiveProject, LibDefinitions.GetMetadataValue("OutputFile"));
				NeedDirOf(OutputFile);

				if(!OneBff)
				{
					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + Fwd(OutputFile) + "\" ";
					}
				}

				ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
				string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile", "TrackerLogDirectory", "MinimalRebuildFromTracking" }, LibDefinitions.Metadata);
				if(!OneBff && !string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					linkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LibrarianOptions = '{0}'\n", Esc("\"%1\" /OUT:\"%2\" " + linkerOptions));
				OutputString.AppendFormat("\t.LibrarianOutput = '{0}'\n", Esc(OutputFile));

				OutputString.Append("\t.LibrarianAdditionalInputs = { ");
				OutputString.Append(string.Join(",", CompileActions));
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}

			if (ActiveProject.GetItems("PostBuildEvent").Any() && !string.IsNullOrEmpty(CurrentProject.OutputNode))
			{
				ProjectItem BuildEvent = ActiveProject.GetItems("PostBuildEvent").First();
				string Command = BuildEvent.GetMetadataValue("Command");
				if(!string.IsNullOrEmpty(Command))
				{
					string BatchText = "call \"" + VcvarsAll + "\" " + VcvarsArch + "\n";
					PostBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_postbuild.bat");
					File.WriteAllText(PostBuildBatchFile, BatchText + Command);
					OutputString.AppendFormat("Exec('{0}postbuild') \n{{\n", N);
					OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", Esc(PostBuildBatchFile));
					OutputString.AppendFormat("\t.ExecInput = '{0}' \n", Esc(PostBuildBatchFile));
					OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", Esc(PostBuildBatchFile + ".txt"));
					OutputString.AppendFormat("\t.PreBuildDependencies = '{0}' \n", CurrentProject.OutputNode);
					OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
					OutputString.Append("}\n\n");
					CurrentProject.OutputNode = N + "postbuild";
				}
			}

			// NuGet native packages hand MSBuild their runtime files as ReferenceCopyLocalPaths,
			// which _CopyFilesMarkedCopyLocal copies into OutDir; the item and include/lib
			// settings arrive already evaluated, so only the copy needs a node.
			if (BuildOutput == BuildType.Application || BuildOutput == BuildType.DynamicLib)
			{
				var CopyLocal = ActiveProject.GetItems("ReferenceCopyLocalPaths")
					.Select(i => ProjectDirRooted(ActiveProject, i.EvaluatedInclude))
					.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
				if (CopyLocal.Any() && !string.IsNullOrEmpty(CurrentProject.OutputNode))
				{
					string OutDir = ProjectDirRooted(ActiveProject, ActiveProject.GetPropertyValue("OutDir"));
					if (!OutDir.EndsWith("\\")) OutDir += "\\";
					// Projects sharing an OutDir copy the same files; FASTBuild allows one node per
					// target, so later projects depend on the node that already owns it.
					var Targets = new List<string> { CurrentProject.OutputNode };
					var Fresh = new List<string>();
					foreach (string Src in CopyLocal)
					{
						string Dst = OutDir + Path.GetFileName(Src);
						string Owner;
						if (CopiedTo.TryGetValue(Dst, out Owner))
						{
							if (!Targets.Contains(Owner)) Targets.Add(Owner);
						}
						else
						{
							CopiedTo[Dst] = N + "copylocal";
							Fresh.Add(Src);
						}
					}
					if (Fresh.Any())
					{
						OutputString.AppendFormat("Copy('{0}copylocal')\n{{\n", N);
						OutputString.Append("\t.Source = { " + string.Join(",", Fresh.Select(c => "'" + Esc(c) + "'")) + " }\n");
						OutputString.AppendFormat("\t.Dest = '{0}'\n", Esc(OutDir));
						OutputString.Append("}\n\n");
						Targets.Add(N + "copylocal");
					}
					OutputString.AppendFormat("Alias('{0}final')\n{{\n\t.Targets = {{ {1} }}\n}}\n\n", N, string.Join(", ", Targets.Select(t => "'" + t + "'")));
					CurrentProject.OutputNode = N + "final";
				}
			}

			if (!OneBff)
			{
				OutputString.Insert(0, MkdirsNode(BFFOutputFilePath));
				foreach (var c in CompilerNodes)
					OutputString.Insert(0, c.Value);
				CompilerNodes.Clear();
				CompilerNodeNames.Clear();
				OutputString.AppendFormat("Alias ('all')\n{{\n\t.Targets = {{ '{0}' }}\n}} ", string.IsNullOrEmpty(CurrentProject.OutputNode) ? "" : CurrentProject.OutputNode);
				File.WriteAllText(BFFOutputFilePath, OutputString.ToString());
			}
			return OutputString.ToString();
		}

		// Output directories the tools expect to exist: MSBuild's MakeDirsForCl/Link create
		// them before compiling; here an Exec node does.
		static Dictionary<string, string> CopiedTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		static SortedSet<string> NeededDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		static readonly HashSet<string> FileOutputProperties = new HashSet<string>
		{
			"ProgramDataBaseFileName", "PrecompiledHeaderOutputFile", "ProgramDatabaseFile", "ImportLibrary", "MapFileName",
			"XMLDocumentationFileName", "BrowseInformationFile", "OutputFile"
		};

		static void NeedDirOf(string PathValue)
		{
			if (string.IsNullOrEmpty(PathValue) || PathValue.IndexOfAny(new[] { '%', '$', '"' }) >= 0 || !Path.IsPathRooted(PathValue))
				return;
			NeededDirs.Add(PathValue.EndsWith("\\") || PathValue.EndsWith("/") ? PathValue.TrimEnd('\\', '/') : Path.GetDirectoryName(PathValue));
		}

		static string MkdirsNode(string BffPath)
		{
			string Bat = BffPath + ".mkdirs.bat";
			File.WriteAllText(Bat, "@echo off\r\n" + string.Join("", NeededDirs.Select(d => "if not exist \"" + d + "\" mkdir \"" + d + "\"\r\n")) + "echo done\r\n");
			NeededDirs.Clear();
			StringBuilder S = new StringBuilder("Exec('mkdirs')\n{\n");
			S.AppendFormat("\t.ExecExecutable = '{0}\\System32\\cmd.exe'\n", Esc(Environment.GetEnvironmentVariable("SystemRoot")));
			S.AppendFormat("\t.ExecArguments = '/c \"{0}\"'\n", Esc(Bat));
			S.AppendFormat("\t.ExecOutput = '{0}'\n", Esc(Bat + ".txt"));
			S.Append("\t.ExecUseStdOutAsOutput = true\n");
			S.Append("\t.ExecAlways = true\n");
			S.Append("}\n\n");
			return S.ToString();
		}

		// Task properties holding paths. MSBuild runs the tools from the project directory;
		// FASTBuild runs them from the bff's, so relative values are rooted at the project.
		static readonly HashSet<string> PathProperties = new HashSet<string>
		{
			"AdditionalIncludeDirectories", "AdditionalUsingDirectories", "ForcedIncludeFiles", "ProgramDataBaseFileName",
			"PrecompiledHeaderOutputFile", "BrowseInformationFile", "XMLDocumentationFileName", "ModuleOutputFile",
			"ModuleDependenciesFile", "SourceDependenciesFile", "AdditionalBMIDirectories", "AdditionalModuleDependencies",
			"AdditionalHeaderUnitDependencies", "ProgramDatabaseFile", "ImportLibrary", "MapFileName", "ManifestFile",
			"AdditionalLibraryDirectories", "ModuleDefinitionFile", "StripPrivateSymbols", "AdditionalManifestDependencies",
			"LinkTimeCodeGenerationObjectFile", "AdditionalDependencies"
		};

		static string RootPathList(string ProjectDir, string Value, bool OnlyExisting)
		{
			var Parts = Value.Split(';').Select(e =>
			{
				string T = e.Trim();
				if (T.Length == 0 || Path.IsPathRooted(T) || T.IndexOfAny(new[] { '%', '$', '"' }) >= 0)
					return T;
				string Rooted = Path.GetFullPath(Path.Combine(ProjectDir, T));
				if (OnlyExisting && !File.Exists(Rooted))
					return T; // A bare library name such as ws2_32.lib, found on LIB.
				if ((T.EndsWith("\\") || T.EndsWith("/")) && !Rooted.EndsWith("\\"))
					Rooted += "\\";
				return Rooted;
			});
			return string.Join(";", Parts);
		}

		public static string GenerateTaskCommandLine(
			ToolTask Task,
			string[] PropertiesToSkip,
			IEnumerable<ProjectMetadata> MetaDataList)
		{
			string ProjectDir = CurrentProject.Proj.DirectoryPath;
			foreach (ProjectMetadata MetaData in MetaDataList)
			{
				if (PropertiesToSkip.Contains(MetaData.Name))
					continue;

				var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
				if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
				{
					string EvaluatedValue = MetaData.EvaluatedValue.Trim();
					if (CommandLineOptions.Z7 && MetaData.Name == "DebugInformationFormat" &&
						(EvaluatedValue == "ProgramDatabase" || EvaluatedValue == "EditAndContinue"))
						EvaluatedValue = "OldStyle";
					if (PathProperties.Contains(MetaData.Name))
						EvaluatedValue = RootPathList(ProjectDir, EvaluatedValue, MetaData.Name == "AdditionalDependencies");
					if (FileOutputProperties.Contains(MetaData.Name) && EvaluatedValue.IndexOfAny(new[] { '%', '$', '"', ';' }) < 0)
						NeedDirOf(ProjectDirRooted(CurrentProject.Proj, EvaluatedValue));
					if(MetaData.Name == "AdditionalIncludeDirectories")
					{
						EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
						EvaluatedValue = EvaluatedValue.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					}

					PropertyInfo propInfo = MatchingProps.First(); //Dubious
					if (!propInfo.CanWrite)
						continue;
					if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
					{
						propInfo.SetValue(Task, EvaluatedValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
					}
					else if (propInfo.PropertyType.IsArray)
					{
						continue; // ITaskItem[] and friends are inputs, not flags.
					}
					else
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
					}
				}
			}

			var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First(); //Dubious
			var Params = GenCmdLineMethod.GetParameters();
			return GenCmdLineMethod.Invoke(Task, Params.Select(p => Type.Missing).ToArray()) as string;
		}
	}

}

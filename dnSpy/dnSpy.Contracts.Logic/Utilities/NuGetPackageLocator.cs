using System;
using System.Collections.Generic;
using System.IO;
using NuGet.Configuration;

namespace dnSpy.Contracts.Utilities {
	/// <summary>
	/// Locates installed NuGet packages.
	/// </summary>
	public static class NuGetPackageLocator {
		struct NuGetPackageInfo {
			public string Name { get; }

			public string? Version { get; }

			public NuGetPackageInfo(string name, string? version) {
				Name = name;
				Version = version;
			}
		}

		static readonly ISettings nugetSettings;

		static NuGetPackageLocator() {
			try {
				nugetSettings = Settings.LoadDefaultSettings(null);
			}
			catch {
				nugetSettings = NullSettings.Instance;
			}
		}

		/// <summary>
		///	Locates possible sources for a given NuGet package
		/// </summary>
		/// <param name="packageName">The name of the NuGet package</param>
		/// <param name="packageVersion">Optional version for direct match</param>
		/// <returns>List of possible sources for the NuGet package</returns>
		public static IReadOnlyList<IReadOnlyList<string>> GetPossibleNuGetPackageLocations(string packageName, string? packageVersion) =>
			GetPossibleNuGetPackageLocations(new NuGetPackageInfo(packageName, packageVersion));

		static List<IReadOnlyList<string>> GetPossibleNuGetPackageLocations(NuGetPackageInfo packageInfo) {
			var packageSources = new List<IReadOnlyList<string>>();

			// This is the directory to which packages are downloaded to
			var globalPackageFolder = SettingsUtility.GetGlobalPackagesFolder(nugetSettings);
			var packages = FindPackages(globalPackageFolder, packageInfo);
			if (packages.Count > 0)
				packageSources.Add(packages);

			// Search fallback package folders, e.g. 'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'
			var fallbackFolders = SettingsUtility.GetFallbackPackageFolders(nugetSettings);
			for (int i = 0; i < fallbackFolders.Count; i++) {
				packages = FindPackages(fallbackFolders[i], packageInfo);
				if (packages.Count > 0)
					packageSources.Add(packages);
			}

			// Search offline package sources, e.g. 'C:\Program Files (x86)\Microsoft SDKs\NuGetPackages' (VS Offline)
			var enabledSources = SettingsUtility.GetEnabledSources(nugetSettings);
			foreach (var packageSource in enabledSources) {
				if (!packageSource.IsLocal)
					continue;
				packages = FindPackages(packageSource.Source, packageInfo);
				if (packages.Count > 0)
					packageSources.Add(packages);
			}

			return packageSources;
		}

		static IReadOnlyList<string> FindPackages(string packageStore, NuGetPackageInfo packageInfo) {
			if (!Directory.Exists(packageStore))
				return Array.Empty<string>();

			var result = new List<string>();

			string[] packages = Directory.GetDirectories(packageStore);
			for (int i = 0; i < packages.Length; i++) {
				string packageFolder = packages[i];
				if (Path.GetFileName(packageFolder) != packageInfo.Name)
					continue;

				string[] versions = Directory.GetDirectories(packageFolder);
				for (int j = 0; j < versions.Length; j++) {
					string packageVersion = versions[j];

					if (packageInfo.Version is null)
						result.Add(packageVersion);
					else if (Path.GetFileName(packageVersion) == packageInfo.Version) {
						result.Add(packageVersion);
						break;
					}
				}

				break;
			}

			return result;
		}
	}
}

/*
    Copyright (C) 2023 ElektroKill

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using dnSpy.Contracts.Settings;

namespace dnSpy.MainApp {
	public readonly struct UpdateCheckInfo {
		public readonly bool Success;
		public readonly bool UpdateAvailable;
		public readonly VersionInfo VersionInfo;

		public UpdateCheckInfo(bool updateAvailable, VersionInfo versionInfo) {
			Success = true;
			UpdateAvailable = updateAvailable;
			VersionInfo = versionInfo;
		}
	}

	public readonly struct VersionInfo {
		public readonly Version Version;
		public readonly string DownloadUrl;

		public VersionInfo(Version version, string url) {
			Version = version;
			DownloadUrl = url;
		}

		public static implicit operator Version(VersionInfo versionInfo) => versionInfo.Version;
	}

	public interface IUpdateService {
		bool CheckForUpdatesOnStartup { get; set; }
		Task<UpdateCheckInfo> CheckForUpdatesAsync();
		void MarkUpdateAsIgnored(Version version);
		bool IsUpdateIgnored(Version version);
		bool CanResetIgnoredUpdates { get; }
		void ResetIgnoredUpdates();
	}

	[Export(typeof(IUpdateService))]
	sealed class UpdateService : IUpdateService {
		static readonly Guid SETTINGS_GUID = new Guid("611A864B-FD4E-4505-8C5F-0165CD09B77A");
		const string IGNORED_SECTION = "Ignored";
		const string IGNORED_ATTR = "id";

		public bool CheckForUpdatesOnStartup {
			get => checkForUpdatesOnStartup;
			set {
				if (checkForUpdatesOnStartup != value) {
					checkForUpdatesOnStartup = value;
					SaveSettings();
				}
			}
		}
		bool checkForUpdatesOnStartup;

		public bool CanResetIgnoredUpdates => ignoredVersions.Count > 0;

		readonly ISettingsService settingsService;
		readonly HashSet<Version> ignoredVersions;

		[ImportingConstructor]
		public UpdateService(ISettingsService settingsService) {
			this.settingsService = settingsService;
			ignoredVersions = new HashSet<Version>();

			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			checkForUpdatesOnStartup = sect.Attribute<bool?>(nameof(CheckForUpdatesOnStartup)) ?? true;

			foreach (var ignoredSect in sect.SectionsWithName(IGNORED_SECTION)) {
				var versionString = ignoredSect.Attribute<string>(IGNORED_ATTR);
				if (!Version.TryParse(versionString, out var version))
					continue;
				ignoredVersions.Add(version);
			}
		}

		static readonly Version currentVersion = GetCurrentVersion();
		static readonly bool isFinalRelease = GetIsFinalRelease();

		static Version GetCurrentVersion() {
			var currentAsm = typeof(StartUpClass).Assembly;
			try {
				var fileVer = FileVersionInfo.GetVersionInfo(currentAsm.Location).FileVersion;
				if (!string2.IsNullOrEmpty(fileVer))
					return new Version(fileVer);
			}
			catch {
			}
			return currentAsm.GetName().Version!;
		}

		static bool GetIsFinalRelease() {
			var currentAsm = typeof(StartUpClass).Assembly;
			try {
				var infoVerAttr = currentAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
				return infoVerAttr?.InformationalVersion is null || !infoVerAttr.InformationalVersion.Contains("-");
			}
			catch {
			}
			return false;
		}

		public async Task<UpdateCheckInfo> CheckForUpdatesAsync() {
			var updateInfo = await TryGetLatestVersionAsync();
			if (updateInfo is not null) {
				if (updateInfo.Value.Version > currentVersion || !isFinalRelease && updateInfo.Value.Version == currentVersion)
					return new UpdateCheckInfo(true, updateInfo.Value);
				return new UpdateCheckInfo(false, default);
			}

			return default;
		}

		static async Task<VersionInfo?> TryGetLatestVersionAsync() {
			try {
				string result;
				using (var client = new HttpClient(new HttpClientHandler { UseProxy = true, UseDefaultCredentials = true })) {
					client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dnSpyEx", currentVersion.ToString()));

					result = await client.GetStringAsync("https://api.github.com/repos/dnSpyEx/dnSpy/releases/latest");
				}

				using var doc = JsonDocument.Parse(result);
				var root = doc.RootElement;
				if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
					return null;
				if (!root.TryGetProperty("html_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
					return null;

				string? tagName = tagElement.GetString();
				string? htmlUrl = urlElement.GetString();
				if (tagName is null || htmlUrl is null)
					return null;

				if (tagName[0] == 'v')
					tagName = tagName.Remove(0, 1);
				if (Version.TryParse(tagName, out var version)) {
					if (version.Revision == -1)
						version = new Version(version.Major, version.Minor, version.Build, 0);
					return new VersionInfo(version, htmlUrl);
				}
			}
			catch {
			}
			return null;
		}

		public void MarkUpdateAsIgnored(Version version) {
			ignoredVersions.Add(version);
			SaveSettings();
		}

		void SaveSettings() {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(CheckForUpdatesOnStartup), CheckForUpdatesOnStartup);
			foreach (var version in ignoredVersions) {
				var ignoredSect = sect.CreateSection(IGNORED_SECTION);
				ignoredSect.Attribute(IGNORED_ATTR, version);
			}
		}

		public bool IsUpdateIgnored(Version version) => ignoredVersions.Contains(version);

		public void ResetIgnoredUpdates() {
			ignoredVersions.Clear();
			SaveSettings();
		}
	}
}

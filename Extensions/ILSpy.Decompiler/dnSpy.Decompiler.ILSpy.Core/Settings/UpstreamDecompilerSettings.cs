/*
    Copyright (C) 2026 dnSpy contributors

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
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Settings;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace dnSpy.Decompiler.ILSpy.Core.Settings {
	/// <summary>
	/// A boolean setting shown in the options dialog and available to dnSpy.Console
	/// </summary>
	sealed class UpstreamSettingInfo {
		/// <summary>
		/// Property name, also used to save the setting
		/// </summary>
		public string Name { get; }
		public string Description { get; }
		/// <summary>
		/// Group shown in the options dialog, eg. "C# 9.0 / VS 2019.8"
		/// </summary>
		public string Group { get; }
		public double GroupOrder { get; }
		/// <summary>
		/// True if it's a C# language feature (changed by the language version presets)
		/// </summary>
		public bool IsLanguageFeature { get; }
		public bool DefaultValue { get; }
		/// <summary>
		/// <see cref="IDecompilerOption.Guid"/>
		/// </summary>
		public Guid Guid { get; }
		/// <summary>
		/// <see cref="IDecompilerOption.Name"/> (command line name)
		/// </summary>
		public string OptionName { get; }

		readonly Func<UpstreamDecompilerSettings, bool> getter;
		readonly Action<UpstreamDecompilerSettings, bool> setter;

		public UpstreamSettingInfo(string name, string description, string group, double groupOrder, bool isLanguageFeature, bool defaultValue, Guid guid, string optionName,
			Func<UpstreamDecompilerSettings, bool> getter, Action<UpstreamDecompilerSettings, bool> setter) {
			Name = name;
			Description = description;
			Group = group;
			GroupOrder = groupOrder;
			IsLanguageFeature = isLanguageFeature;
			DefaultValue = defaultValue;
			Guid = guid;
			OptionName = optionName;
			this.getter = getter;
			this.setter = setter;
		}

		public bool GetValue(UpstreamDecompilerSettings settings) => getter(settings);
		public void SetValue(UpstreamDecompilerSettings settings, bool value) => setter(settings, value);
		public override string ToString() => Name;
	}

	/// <summary>
	/// Settings of the upstream C# decompiler: the upstream <see cref="DecompilerSettings"/> plus dnSpy's own options.
	/// The list of settings is created from the upstream settings' attributes so new upstream settings show up
	/// without any changes here.
	/// </summary>
	sealed class UpstreamDecompilerSettings : DecompilerSettingsBase {
		public const string DnSpyGroup = "dnSpy";
		const string OtherGroup = "Other";

		/// <summary>
		/// The upstream settings. Don't modify the instance while it's being used to decompile, decompile with a clone.
		/// </summary>
		public DecompilerSettings Settings { get; }

		/// <summary>
		/// Add "// Token: 0x06000001 RID: 1 RVA: 0x00002050 File Offset: 0x00000250" comments
		/// </summary>
		public bool ShowTokenAndRvaComments {
			get => showTokenAndRvaComments;
			set {
				if (showTokenAndRvaComments != value) {
					showTokenAndRvaComments = value;
					OnModified();
				}
			}
		}
		bool showTokenAndRvaComments = true;

		/// <summary>
		/// Show compiler generated types and members (closures, state machines, backing fields...) in the tree view
		/// </summary>
		public bool ShowHiddenMembers {
			get => showHiddenMembers;
			set {
				if (showHiddenMembers != value) {
					showHiddenMembers = value;
					OnModified();
				}
			}
		}
		bool showHiddenMembers;

		public override int Version => version;
		// The debugger caches debug info by method and compares this version with the version of the decompiler
		// it uses, so don't start where other decompilers' settings start (0)
		int version = 0x3D000000;

		public override event EventHandler? VersionChanged;

		public static IReadOnlyList<UpstreamSettingInfo> AllSettings => allSettings.Value;
		static readonly Lazy<UpstreamSettingInfo[]> allSettings = new Lazy<UpstreamSettingInfo[]>(CreateSettingInfos);

		public override IEnumerable<IDecompilerOption> Options { get; }

		public UpstreamDecompilerSettings() : this(CreateDefaultUpstreamSettings()) { }

		UpstreamDecompilerSettings(DecompilerSettings settings) {
			Settings = settings;
			Settings.PropertyChanged += (s, e) => OnModified();
			Options = AllSettings.Select(info => (IDecompilerOption)new DecompilerOption<bool>(info.Guid, () => info.GetValue(this), value => info.SetValue(this, value)) {
				Description = info.Description,
				Name = info.OptionName,
			}).ToArray();
		}

		static DecompilerSettings CreateDefaultUpstreamSettings() {
			var settings = new DecompilerSettings(LanguageVersion.Latest);
			ApplyFixedSettings(settings);
			return settings;
		}

		/// <summary>
		/// Settings that aren't shown because dnSpy needs a specific value
		/// </summary>
		static void ApplyFixedSettings(DecompilerSettings settings) {
			// Obfuscated and malware assemblies often reference assemblies that aren't available
			settings.ThrowOnAssemblyResolveErrors = false;
		}

		/// <summary>
		/// Copy of the settings used for one decompilation (the user can change the settings while it runs)
		/// </summary>
		public DecompilerSettings CreateDecompilationSettings() {
			var clone = Settings.Clone();
			ApplyFixedSettings(clone);
			return clone;
		}

		void OnModified() {
			version++;
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		public override DecompilerSettingsBase Clone() {
			var clone = new UpstreamDecompilerSettings(Settings.Clone());
			clone.showTokenAndRvaComments = showTokenAndRvaComments;
			clone.showHiddenMembers = showHiddenMembers;
			return clone;
		}

		public void CopyTo(UpstreamDecompilerSettings other) {
			foreach (var info in AllSettings)
				info.SetValue(other, info.GetValue(this));
		}

		/// <summary>
		/// Enables the C# language features up to <paramref name="languageVersion"/> and disables newer ones
		/// </summary>
		public void SetLanguageVersion(LanguageVersion languageVersion) {
			var reference = new UpstreamDecompilerSettings(new DecompilerSettings(languageVersion));
			foreach (var info in AllSettings) {
				if (info.IsLanguageFeature)
					info.SetValue(this, info.GetValue(reference));
			}
		}

		/// <summary>
		/// The lowest C# version that has all enabled language features
		/// </summary>
		public LanguageVersion GetMinimumRequiredVersion() => Settings.GetMinimumRequiredVersion();

		public override bool Equals(object? obj) =>
			obj is UpstreamDecompilerSettings other && AllSettings.All(info => info.GetValue(this) == info.GetValue(other));

		public override int GetHashCode() {
			int hash = 0;
			int i = 0;
			foreach (var info in AllSettings) {
				if (info.GetValue(this))
					hash ^= 1 << (i % 31);
				i++;
			}
			return hash;
		}

		static UpstreamSettingInfo[] CreateSettingInfos() {
			var list = new List<UpstreamSettingInfo> {
				new UpstreamSettingInfo(nameof(ShowTokenAndRvaComments), "Show tokens, RVAs and file offsets", DnSpyGroup, 0, false, true,
					DecompilerOptionConstants.ShowTokenAndRvaComments_GUID, DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
					s => s.ShowTokenAndRvaComments, (s, v) => s.ShowTokenAndRvaComments = v),
				new UpstreamSettingInfo(nameof(ShowHiddenMembers), "Show hidden compiler generated types and members in the tree view", DnSpyGroup, 0, false, false,
					DecompilerOptionConstants.ForceShowAllMembers_GUID, DecompilerOptionConstants.ForceShowAllMembers_NAME,
					s => s.ShowHiddenMembers, (s, v) => s.ShowHiddenMembers = v),
			};

			var knownOptions = GetKnownOptions();
			var defaults = new DecompilerSettings(LanguageVersion.Latest);
			foreach (var prop in typeof(DecompilerSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
				if (prop.PropertyType != typeof(bool) || !prop.CanRead || !prop.CanWrite || prop.GetSetMethod() is null || prop.GetIndexParameters().Length != 0)
					continue;
				if (!IsShown(prop, out var category))
					continue;
				var description = GetDescription(prop);
				bool isLanguageFeature = category.StartsWith("C# ", StringComparison.Ordinal);
				string group;
				double groupOrder;
				if (isLanguageFeature) {
					group = category;
					groupOrder = 1 + GetLanguageVersionOrder(category);
				}
				else {
					group = OtherGroup;
					groupOrder = 1000;
				}

				Guid guid;
				string optionName;
				if (knownOptions.TryGetValue(prop.Name, out var known))
					(guid, optionName) = known;
				else {
					optionName = ToOptionName(prop.Name);
					guid = CreateGuid(prop.Name);
				}

				var getter = (Func<DecompilerSettings, bool>)Delegate.CreateDelegate(typeof(Func<DecompilerSettings, bool>), prop.GetGetMethod()!);
				var setter = (Action<DecompilerSettings, bool>)Delegate.CreateDelegate(typeof(Action<DecompilerSettings, bool>), prop.GetSetMethod()!);
				list.Add(new UpstreamSettingInfo(prop.Name, description, group, groupOrder, isLanguageFeature, getter(defaults), guid, optionName,
					s => getter(s.Settings), (s, v) => setter(s.Settings, v)));
			}
			return list.OrderBy(a => a.GroupOrder).ThenBy(a => a.Group, StringComparer.Ordinal).ThenBy(a => a.Description, StringComparer.CurrentCultureIgnoreCase).ToArray();
		}

		// Settings that have no effect in dnSpy or that are set by dnSpy
		static readonly HashSet<string> notShown = new HashSet<string>(StringComparer.Ordinal) {
			nameof(DecompilerSettings.ThrowOnAssemblyResolveErrors),
			nameof(DecompilerSettings.LoadInMemory),
			nameof(DecompilerSettings.AutoLoadAssemblyReferences),
			nameof(DecompilerSettings.ApplyWindowsRuntimeProjections),
		};

		static bool IsShown(PropertyInfo prop, out string category) {
			category = prop.GetCustomAttribute<CategoryAttribute>()?.Category ?? string.Empty;
			if (notShown.Contains(prop.Name))
				return false;
			if (prop.GetCustomAttribute<BrowsableAttribute>() is BrowsableAttribute browsable && !browsable.Browsable)
				return false;
			if (prop.GetCustomAttribute<ObsoleteAttribute>() is not null)
				return false;
			if (prop.GetCustomAttribute<DescriptionAttribute>() is null)
				return false;
			// dnSpy writes the projects itself (the package's project export options do nothing) and doesn't decompile to
			// Visual Basic
			if (category == "DecompilerSettings.ProjectExport" || category == "DecompilerSettings.VBSpecificOptions")
				return false;
			return true;
		}

		static string GetDescription(PropertyInfo prop) {
			var key = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
			return SettingDescriptions.TryGet(key) ?? SettingDescriptions.Humanize(prop.Name);
		}

		static double GetLanguageVersionOrder(string category) {
			// "C# 9.0 / VS 2019.8"
			var s = category.Substring("C# ".Length);
			int end = s.IndexOf(' ');
			if (end >= 0)
				s = s.Substring(0, end);
			return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var version) ? version : 99;
		}

		/// <summary>
		/// The options dnSpy's previous C# decompiler also has keep their GUIDs and names
		/// </summary>
		static Dictionary<string, (Guid guid, string name)> GetKnownOptions() {
			var dict = new Dictionary<string, (Guid, string)>(StringComparer.Ordinal);
			const string nameSuffix = "_NAME";
			const string guidSuffix = "_GUID";
			var fields = typeof(DecompilerOptionConstants).GetFields(BindingFlags.Public | BindingFlags.Static);
			foreach (var field in fields) {
				if (!field.Name.EndsWith(nameSuffix, StringComparison.Ordinal) || !(field.GetValue(null) is string name))
					continue;
				var propName = field.Name.Substring(0, field.Name.Length - nameSuffix.Length);
				var guidField = fields.FirstOrDefault(a => a.Name == propName + guidSuffix);
				if (guidField?.GetValue(null) is Guid guid)
					dict[propName] = (guid, name);
			}
			return dict;
		}

		/// <summary>
		/// "NativeIntegers" -> "native-integers"
		/// </summary>
		static string ToOptionName(string name) {
			var sb = new StringBuilder(name.Length + 8);
			for (int i = 0; i < name.Length; i++) {
				char c = name[i];
				if (char.IsUpper(c)) {
					if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
						sb.Append('-');
					sb.Append(char.ToLowerInvariant(c));
				}
				else
					sb.Append(c);
			}
			return sb.ToString();
		}

		/// <summary>
		/// A GUID that's the same in every dnSpy version
		/// </summary>
		static Guid CreateGuid(string name) {
			using var md5 = MD5.Create();
			var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("dnSpy.Decompiler.ILSpy.Core.Settings." + name));
			return new Guid(hash);
		}
	}
}

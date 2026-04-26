/*
    Copyright (C) 2026 ElektroKill

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

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace dnSpy.Documents {
	class RuntimeConfigFile {
		public string? TargetFrameworkMoniker { get; private set; }

		public List<RuntimeFramework> Frameworks { get; } = [];

		public List<RuntimeFramework> IncludedFrameworks { get; }  = [];

		public List<string> AdditionalProbingPaths { get; } = [];

		public static RuntimeConfigFile? Read(string fileName) {
			if (!File.Exists(fileName))
				return null;

			using var fs = File.OpenRead(fileName);
			using var document = JsonDocument.Parse(fs);
			var root = document.RootElement;

			if (!root.TryGetProperty("runtimeOptions", out var runtimeOptions) || runtimeOptions.ValueKind != JsonValueKind.Object)
				return null;

			var config = new RuntimeConfigFile();

			if (runtimeOptions.TryGetProperty("tfm", out var tfm) && tfm.ValueKind == JsonValueKind.String)
				config.TargetFrameworkMoniker = tfm.GetString();

			if (runtimeOptions.TryGetProperty("framework", out var framework) && framework.ValueKind == JsonValueKind.Object)
				config.Frameworks.Add(ParseRuntimeFramework(framework));

			if (runtimeOptions.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Array) {
				foreach (var element in frameworks.EnumerateArray()) {
					if (element.ValueKind != JsonValueKind.Object)
						continue;
					config.Frameworks.Add(ParseRuntimeFramework(element));
				}
			}

			if (runtimeOptions.TryGetProperty("includedFrameworks", out var includedFrameworks) && includedFrameworks.ValueKind == JsonValueKind.Array) {
				foreach (var element in includedFrameworks.EnumerateArray()) {
					if (element.ValueKind != JsonValueKind.Object)
						continue;
					config.IncludedFrameworks.Add(ParseRuntimeFramework(element));
				}
			}

			if (runtimeOptions.TryGetProperty("additionalProbingPaths", out var additionalProbingPaths) && additionalProbingPaths.ValueKind == JsonValueKind.Array) {
				foreach (var element in additionalProbingPaths.EnumerateArray()) {
					if (element.ValueKind != JsonValueKind.String)
						continue;
					var path = element.GetString();
					if (path is not null)
						config.AdditionalProbingPaths.Add(path);
				}
			}

			return config;

			static RuntimeFramework ParseRuntimeFramework(JsonElement element) {
				var runtimeFramework = new RuntimeFramework();
				if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
					runtimeFramework.Name = name.GetString();
				if (element.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
					runtimeFramework.Version = version.GetString();
				return runtimeFramework;
			}
		}

	}

	struct RuntimeFramework {
		public string? Name { get; set; }
		public string? Version { get; set; }
	}
}

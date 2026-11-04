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
using System.IO;
using System.Linq;
using dnSpy.UITests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.UITests {
	/// <summary>
	/// File → Export to Project... with dnSpy's dialog, then the project is built and run
	/// </summary>
	public sealed class ExportTests : UITest {
		public ExportTests(DnSpyFixture fixture, ITestOutputHelper log) : base(fixture, log) { }

		const string Dialog = "ExportToProjectDlg";

		bool TryClose() {
			try {
				App.Call("click", new System.Text.Json.Nodes.JsonObject { ["window"] = Dialog, ["button"] = "Close" });
				return true;
			}
			catch (ProbeCallException) {
				return false;
			}
		}

		string Export(string file, bool sdkStyle) {
			App.Show(file, "<assembly>", "C#");
			App.Menu("File", "Export to Project...");
			App.WaitForDialog(Dialog);
			var dir = Samples.NewWorkDirectory("export-" + Path.GetFileNameWithoutExtension(file));
			App.SetText(Dialog, "destDirTextBox", dir);
			App.SetChecked(Dialog, "Use SDK style project format", sdkStyle);
			App.SetChecked(Dialog, "Open project in Visual Studio", false);
			App.Click(Dialog, "Export");
			// When it's done the dialog closes (or shows Close if there were errors)
			App.WaitUntil(() => !App.Dialogs().Any(a => a.type.EndsWith(Dialog, StringComparison.Ordinal)) || TryClose(), "the export", TimeSpan.FromMinutes(5));
			App.WaitForDialogClosed(Dialog);
			var project = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories).Single();
			Log.WriteLine(File.ReadAllText(project));
			return project;
		}

		/// <summary>
		/// A classic project can't target .NET (Visual Studio can't load it): .NET assemblies get an SDK-style project
		/// even with "SDK style" unchecked. Built and run, it prints the same as the original.
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void Export_DotNet10_IsSdkStyle_BuildsAndRuns(bool sdkStyleChecked) {
			var file = Samples.ConsoleSample();
			var project = Export(file, sdkStyleChecked);
			var xml = File.ReadAllText(project);
			Assert.Contains("<Project Sdk=", xml);
			Assert.DoesNotContain("<TargetFrameworkVersion>", xml);

			var outDir = Path.Combine(Path.GetDirectoryName(project)!, "out");
			var (exitCode, output) = Samples.Run("dotnet", Path.GetDirectoryName(project)!, "build", project, "-c", "Release", "-o", outDir, "-nologo", "-v", "q");
			Assert.True(exitCode == 0, output);
			var built = Path.Combine(outDir, Path.GetFileName(file));
			Assert.Equal(Samples.RunProgram(file), Samples.RunProgram(built));
		}

		/// <summary>
		/// .NET Framework with "SDK style" unchecked: a classic project
		/// </summary>
		[Fact]
		public void Export_NetFramework_SdkStyleUnchecked_IsClassic() {
			var project = Export(Samples.NetFxSample(), sdkStyle: false);
			var xml = File.ReadAllText(project);
			Assert.DoesNotContain("<Project Sdk=", xml);
			Assert.Contains("<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>", xml);
		}
	}
}

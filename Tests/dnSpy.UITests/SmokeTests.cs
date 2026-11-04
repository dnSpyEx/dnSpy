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

using System.Linq;
using dnSpy.UITests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.UITests {
	/// <summary>
	/// The harness itself: dnSpy starts with the probe, shows code, and the probe sees it
	/// </summary>
	public sealed class SmokeTests : UITest {
		public SmokeTests(DnSpyFixture fixture, ITestOutputHelper log) : base(fixture, log) { }

		[Fact]
		public void Languages_AreTheExpectedOnes() {
			var languages = App.Call("languages")!.AsArray().Select(a => (string)a!).ToList();
			Log.WriteLine(string.Join(", ", languages));
			Assert.Contains("C#", languages);
			Assert.Contains("IL", languages);
			Assert.Contains("IL with C#", languages);
			Assert.DoesNotContain(languages, a => a.Contains("Visual Basic") || a.Contains("VB"));
			Assert.Equal(languages.Count, languages.Distinct().Count());
		}

		[Fact]
		public void ShowsTheDecompiledCode() {
			var file = Samples.DebuggerPlayground("Release");
			var tab = App.Show(file, "DebuggerPlayground.Scenarios::Switch", "C#");
			Assert.Contains("public static int Switch(int value)", tab.Text);
			Assert.Contains("return 10;", tab.Text);
		}
	}
}

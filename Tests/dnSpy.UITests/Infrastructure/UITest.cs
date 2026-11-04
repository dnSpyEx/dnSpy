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
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// One dnSpy for all the tests (started again if one crashed it)
	/// </summary>
	public sealed class DnSpyFixture : IDisposable {
		DnSpyApp? app;

		internal DnSpyApp App {
			get {
				if (app is null || app.HasExited) {
					app?.Dispose();
					app = DnSpyApp.Start();
				}
				return app;
			}
		}

		internal void Restart() {
			app?.Dispose();
			app = null;
		}

		public void Dispose() => app?.Dispose();
	}

	[CollectionDefinition(Name)]
	public sealed class DnSpyCollection : ICollectionFixture<DnSpyFixture> {
		public const string Name = "dnSpy";
	}

	/// <summary>
	/// Each test starts with an empty dnSpy, and fails if dnSpy threw an unhandled exception or left a dialog open
	/// </summary>
	[Collection(DnSpyCollection.Name)]
	public abstract class UITest : IDisposable {
		readonly DnSpyFixture fixture;
		protected readonly ITestOutputHelper Log;
		internal DnSpyApp App { get; }

		protected UITest(DnSpyFixture fixture, ITestOutputHelper log) {
			this.fixture = fixture;
			Log = log;
			App = fixture.App;
			try {
				App.Reset();
			}
			catch {
				// A previous test left dnSpy in a bad state
				fixture.Restart();
				App = fixture.App;
			}
		}

		public void Dispose() {
			if (App.HasExited) {
				fixture.Restart();
				throw new InvalidOperationException("dnSpy exited (crashed?) during the test");
			}
			var problems = App.TakeProblems();
			foreach (var known in App.TakeKnownIssues())
				Log.WriteLine("Known issue: " + known);
			if (problems.Count != 0) {
				var screenshot = App.Screenshot("problems");
				try {
					App.Reset();
				}
				catch {
					fixture.Restart();
				}
				throw new InvalidOperationException($"dnSpy problems during the test (screenshot: {screenshot}):\n" + string.Join("\n", problems));
			}
		}
	}
}

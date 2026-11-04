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
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// Talks to the probe extension inside dnSpy (Tests\dnSpy.UITests.Probe)
	/// </summary>
	sealed class ProbeClient : IDisposable {
		readonly NamedPipeClientStream pipe;
		readonly StreamReader reader;
		readonly StreamWriter writer;

		ProbeClient(NamedPipeClientStream pipe) {
			this.pipe = pipe;
			reader = new StreamReader(pipe, new UTF8Encoding(false));
			writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
		}

		public static ProbeClient Connect(string pipeName, Process process, TimeSpan timeout) {
			var watch = Stopwatch.StartNew();
			while (true) {
				if (process.HasExited)
					throw new InvalidOperationException($"dnSpy exited (code {process.ExitCode}) before the probe started");
				var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
				try {
					pipe.Connect(1000);
					return new ProbeClient(pipe);
				}
				catch (TimeoutException) {
					pipe.Dispose();
				}
				catch (IOException) {
					pipe.Dispose();
				}
				if (watch.Elapsed > timeout)
					throw new TimeoutException("The probe didn't start: is dnSpy built, and did it load Probe\\dnSpy.UITests.Probe.x.dll?");
				Thread.Sleep(250);
			}
		}

		public JsonNode? Call(string cmd, JsonObject? args = null) {
			var request = new JsonObject { ["cmd"] = cmd, ["args"] = args ?? new JsonObject() };
			lock (this) {
				writer.WriteLine(request.ToJsonString());
				var line = reader.ReadLine() ?? throw new IOException("The probe closed the connection (did dnSpy crash?)");
				var response = JsonNode.Parse(line)!.AsObject();
				if (!(bool)response["ok"]!)
					throw new ProbeCallException(cmd, (string?)response["error"] ?? "?");
				return response["result"];
			}
		}

		public void Dispose() => pipe.Dispose();
	}

	sealed class ProbeCallException : Exception {
		public ProbeCallException(string cmd, string message) : base($"probe '{cmd}': {message}") { }
	}
}

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
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace dnSpy.UITests.Probe {
	/// <summary>
	/// One JSON object per line each way: {"cmd": "...", "args": {...}} → {"ok": true, "result": ...} or
	/// {"ok": false, "error": "..."}. One client at a time; it reconnects after a disconnect.
	/// </summary>
	sealed class ProbeServer : IDisposable {
		readonly string pipeName;
		readonly Commands commands;
		readonly CancellationTokenSource cts = new CancellationTokenSource();

		public ProbeServer(string pipeName, Commands commands) {
			this.pipeName = pipeName;
			this.commands = commands;
		}

		public void Start() => new Thread(Run) { IsBackground = true, Name = "dnSpy UI tests probe" }.Start();

		void Run() {
			while (!cts.IsCancellationRequested) {
				try {
					using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
					pipe.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
					using var reader = new StreamReader(pipe, new UTF8Encoding(false));
					using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
					while (!cts.IsCancellationRequested) {
						var line = reader.ReadLine();
						if (line is null)
							break;
						writer.WriteLine(Handle(line));
					}
				}
				catch (OperationCanceledException) {
					return;
				}
				catch (IOException) {
					// Client went away
				}
			}
		}

		string Handle(string line) {
			var response = new JsonObject();
			try {
				var request = JsonNode.Parse(line)!.AsObject();
				var cmd = (string?)request["cmd"] ?? throw new ProbeException("No 'cmd'");
				var args = request["args"] as JsonObject ?? new JsonObject();
				response["ok"] = true;
				response["result"] = commands.Execute(cmd, args);
			}
			catch (Exception ex) {
				response["ok"] = false;
				response["error"] = ex is ProbeException ? ex.Message : ex.ToString();
			}
			return response.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
		}

		public void Dispose() => cts.Cancel();
	}

	sealed class ProbeException : Exception {
		public ProbeException(string message) : base(message) { }
	}
}

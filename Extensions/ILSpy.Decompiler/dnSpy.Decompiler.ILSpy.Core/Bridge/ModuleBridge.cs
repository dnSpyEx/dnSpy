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
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Which image the upstream decompiler reads
	/// </summary>
	enum BridgeImageKind {
		/// <summary>
		/// The module's original bytes, tokens are identical to dnlib's tokens
		/// </summary>
		Original,

		/// <summary>
		/// An image written by dnlib from the in-memory module (edited, dynamic, or anomalous metadata)
		/// </summary>
		Normalized,
	}

	sealed class ModuleBridgeOptions {
		/// <summary>
		/// Always let dnlib rewrite the module, even if the original image looks safe to read
		/// </summary>
		public bool AlwaysNormalize { get; set; }

		/// <summary>
		/// Try to preserve rids when normalizing (tokens then match what dnSpy shows). Tests turn it off to
		/// exercise the fallback.
		/// </summary>
		internal bool PreserveRids { get; set; } = true;
	}

	/// <summary>
	/// A dnlib module made readable by the upstream decompiler
	/// </summary>
	sealed class BridgedModule : IDisposable {
		public ModuleDef Module { get; }
		public MetadataFile File { get; }
		public ITokenMap Tokens { get; }
		public BridgeImageKind ImageKind { get; }

		/// <summary>
		/// Why the module was normalized (empty if <see cref="ImageKind"/> is <see cref="BridgeImageKind.Original"/>)
		/// </summary>
		public IReadOnlyList<string> NormalizeReasons { get; }

		/// <summary>
		/// Errors dnlib reported while writing the normalized image. dnlib writes something (e.g. a 0 branch
		/// offset) instead of failing, so the IL of the affected members can be wrong in this image.
		/// </summary>
		public IReadOnlyList<string> WriterErrors { get; }

		/// <summary>
		/// Time it took to check and (if needed) rewrite the module
		/// </summary>
		public TimeSpan CreationTime { get; }

		public BridgedModule(ModuleDef module, MetadataFile file, ITokenMap tokens, BridgeImageKind imageKind, IReadOnlyList<string> normalizeReasons, IReadOnlyList<string> writerErrors, TimeSpan creationTime) {
			Module = module;
			File = file;
			Tokens = tokens;
			ImageKind = imageKind;
			NormalizeReasons = normalizeReasons;
			WriterErrors = writerErrors;
			CreationTime = creationTime;
		}

		/// <summary>
		/// True once the cache dropped it (the module was edited, reloaded or removed). Anything built from it
		/// (eg. a type system) is out of date.
		/// </summary>
		public bool IsInvalidated {
			get => isInvalidated;
			internal set => isInvalidated = value;
		}
		volatile bool isInvalidated;

		public void Dispose() => File.Dispose();
	}

	sealed class ModuleBridgeException : Exception {
		public ModuleBridgeException(string message, Exception? innerException) : base(message, innerException) { }
	}

	static class ModuleBridge {
		/// <summary>
		/// Creates an image of <paramref name="module"/> that the upstream decompiler can read
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="isModified">true if the module has been edited in memory at any time since it was loaded,
		/// even if it has been saved since (dnSpy doesn't reload saved modules, the original image is then stale)</param>
		/// <param name="options">Options</param>
		public static BridgedModule Create(ModuleDef module, bool isModified, ModuleBridgeOptions options) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (options is null)
				throw new ArgumentNullException(nameof(options));
			var sw = Stopwatch.StartNew();
			var reasons = new List<string>();
			if (options.AlwaysNormalize)
				reasons.Add("Normalization is always enabled");
			if (isModified)
				reasons.Add("Module was modified in memory");
			var mdModule = module as ModuleDefMD;
			if (mdModule is null)
				reasons.Add("Module wasn't read from metadata (created or emitted in memory)");

			if (reasons.Count == 0 && mdModule is not null) {
				if (HasMembersNotInImage(mdModule))
					reasons.Add("Module has members that aren't in its original image");
			}
			if (reasons.Count == 0 && mdModule is not null) {
				reasons.AddRange(MetadataAnomalyDetector.GetStructuralAnomalies(mdModule));
				if (reasons.Count == 0) {
					PEFile? file = null;
					try {
						file = OpenOriginal(mdModule);
						reasons.AddRange(MetadataAnomalyDetector.CompareWithDnlib(mdModule, file.Metadata));
					}
					catch (Exception ex) when (IsImageReadException(ex)) {
						reasons.Add("System.Reflection.Metadata can't read the original image: " + ex.Message);
					}
					if (reasons.Count == 0 && file is not null)
						return new BridgedModule(module, file, new IdentityTokenMap(module), BridgeImageKind.Original, Array.Empty<string>(), Array.Empty<string>(), sw.Elapsed);
					file?.Dispose();
				}
			}

			return Normalize(module, reasons, sw, options.PreserveRids);
		}

		static BridgedModule Normalize(ModuleDef module, List<string> reasons, Stopwatch sw, bool preserveRidsFirst) {
			Exception? firstError = null;
			// Preserving rids keeps tokens stable (same as what dnSpy shows), but it can fail on some
			// obfuscated modules. Fall back to fresh rids; the token map works either way.
			foreach (bool preserveRids in preserveRidsFirst ? new[] { true, false } : new[] { false }) {
				try {
					var logger = new CollectingLogger();
					var (bytes, metadata) = Write(module, preserveRids, logger);
					var map = RewriteTokenMap.Create(module, metadata);
					var file = new PEFile(GetFileName(module), new MemoryStream(bytes, writable: false), PEStreamOptions.PrefetchEntireImage);
					var allReasons = new List<string>(reasons);
					if (!preserveRids && preserveRidsFirst)
						allReasons.Add("Rids couldn't be preserved: " + firstError?.Message);
					if (logger.Errors.Count != 0)
						allReasons.Add($"dnlib reported {logger.Errors.Count} error(s) while writing, the IL of some members may be wrong: {logger.Errors[0]}");
					return new BridgedModule(module, file, map, BridgeImageKind.Normalized, allReasons, logger.Errors, sw.Elapsed);
				}
				catch (Exception ex) when (ex is not OutOfMemoryException) {
					firstError ??= ex;
				}
			}
			throw new ModuleBridgeException($"dnlib couldn't write a decompilable image of module '{module.Name}'", firstError);
		}

		static (byte[] bytes, Metadata metadata) Write(ModuleDef module, bool preserveRids, ILogger logger) {
			var options = new ModuleWriterOptions(module) {
				// Don't fail because of an invalid member, we'd rather decompile the rest. Errors are collected.
				Logger = logger,
				WritePdb = false,
				StrongNameKey = null,
				StrongNamePublicKey = null,
				DelaySign = false,
			};
			options.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
			if (preserveRids)
				options.MetadataOptions.Flags |= MetadataFlags.PreserveRids;
			var writer = new ModuleWriter(module, options);
			var stream = new MemoryStream();
			writer.Write(stream);
			return (stream.ToArray(), writer.Metadata);
		}

		/// <summary>
		/// Members created in memory (rid 0) or rows that don't resolve back to the same object
		/// </summary>
		static bool HasMembersNotInImage(ModuleDefMD module) {
			foreach (var type in module.GetTypes()) {
				if (!InImage(module, type))
					return true;
				foreach (var m in type.Methods) {
					if (!InImage(module, m))
						return true;
				}
				foreach (var f in type.Fields) {
					if (!InImage(module, f))
						return true;
				}
				foreach (var p in type.Properties) {
					if (!InImage(module, p))
						return true;
				}
				foreach (var e in type.Events) {
					if (!InImage(module, e))
						return true;
				}
			}
			return false;
		}

		static bool InImage(ModuleDefMD module, IMDTokenProvider def) =>
			def.MDToken.Rid != 0 && ReferenceEquals(module.ResolveToken(def.MDToken.Raw), def);

		sealed class CollectingLogger : ILogger {
			public List<string> Errors { get; } = new List<string>();

			public void Log(object? sender, LoggerEvent loggerEvent, string format, params object?[] args) {
				if (loggerEvent != LoggerEvent.Error || Errors.Count >= 100)
					return;
				string message;
				try {
					message = string.Format(format, args);
				}
				catch (FormatException) {
					message = format;
				}
				Errors.Add(message);
			}

			public bool IgnoresEvent(LoggerEvent loggerEvent) => loggerEvent != LoggerEvent.Error;
		}

		internal static PEFile OpenOriginal(ModuleDefMD module) {
			var peImage = module.Metadata.PEImage;
			var bytes = peImage.CreateReader().ToArray();
			var streamOptions = PEStreamOptions.PrefetchEntireImage;
			// Modules loaded from a process' memory use the memory layout
			if (!peImage.IsFileImageLayout)
				streamOptions |= PEStreamOptions.IsLoadedImage;
			return new PEFile(GetFileName(module), new MemoryStream(bytes, writable: false), streamOptions);
		}

		static string GetFileName(ModuleDef module) {
			if (!string.IsNullOrEmpty(module.Location))
				return module.Location;
			var name = UTF8String.ToSystemStringOrEmpty(module.Name);
			return name.Length != 0 ? name : "module.dll";
		}

		static bool IsImageReadException(Exception ex) =>
			ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException ||
			ex is IndexOutOfRangeException || ex is NotSupportedException || ex is OverflowException ||
			ex is IOException || ex is MetadataFileNotSupportedException;
	}
}

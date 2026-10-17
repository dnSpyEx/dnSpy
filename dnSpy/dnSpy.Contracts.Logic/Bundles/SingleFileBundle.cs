using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.IO;
using dnlib.PE;
using dnlib.Utils;

namespace dnSpy.Contracts.Bundles {
	/// <summary>
	///
	/// </summary>
	public sealed class SingleFileBundle : IListListener<BundleEntry>, IListListener<BundleFolder> {
		// 32 byte SHA-256 for ".net core bundle"
		static readonly byte[] bundleSignature = {
			0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
			0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
			0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
			0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
		};

		readonly DataReaderFactory dataReaderFactory;
		readonly int originalEntryCount;
		readonly uint originalMajorVersion;
		readonly uint entryOffset;
		readonly ModuleCreationOptions moduleCreationOptions;

		/// <summary>
		/// The major version of the bundle.
		/// </summary>
		public uint MajorVersion { get; }

		/// <summary>
		/// The minor version of the bundle.
		/// </summary>
		public uint MinorVersion { get; }

		/// <summary>
		/// Number of entries in the bundle.
		/// </summary>
		public int EntryCount { get; }

		/// <summary>
		/// ID of the bundle.
		/// </summary>
		public string BundleID { get; }

		/// <summary>
		/// Bundle flags
		/// Only present in version 2.0 and above.
		/// </summary>
		public ulong Flags { get; }

		/// <summary>
		/// All of the entries present in the bundle
		/// </summary>
		public IEnumerable<BundleEntry> Entries {
			get {
				for (int i = 0; i < TopLevelEntries.Count; i++)
					yield return TopLevelEntries[i];

				var stack = new Stack<IList<BundleFolder>>();
				stack.Push(TopLevelFolders);

				while (stack.Count > 0) {
					var folders = stack.Pop();
					for (int i = 0; i < folders.Count; i++) {
						var folder = folders[i];
						for (int j = 0; j < folder.Entries.Count; j++)
							yield return folder.Entries[j];
						stack.Push(folder.NestedFolders);
					}
				}
			}
		}

		/// <summary>
		/// The top level entries present in the bundle
		/// </summary>
		public IList<BundleEntry> TopLevelEntries {
			get {
				if (topLevelEntries is not null)
					return topLevelEntries;
				InitializeBundleEntriesAndFolder();
				return topLevelEntries!;
			}
		}
		LazyList<BundleEntry>? topLevelEntries;

		/// <summary>
		/// The top level folders present in the bundle.
		/// </summary>
		public IList<BundleFolder> TopLevelFolders {
			get {
				if (topLevelFolders is not null)
					return topLevelFolders;
				InitializeBundleEntriesAndFolder();
				return topLevelFolders!;
			}

		}
		LazyList<BundleFolder>? topLevelFolders;

		SingleFileBundle(DataReaderFactory dataReaderFactory, uint headerOffset, ModuleCreationOptions moduleCreationOptions) {
			this.dataReaderFactory = dataReaderFactory;
			this.moduleCreationOptions = moduleCreationOptions;
			var reader = dataReaderFactory.CreateReader();
			reader.Position = headerOffset;
			MajorVersion = originalMajorVersion = reader.ReadUInt32();
			MinorVersion = reader.ReadUInt32();
			EntryCount = originalEntryCount = reader.ReadInt32();
			BundleID = reader.ReadSerializedString();
			if (MajorVersion >= 2) {
				var depsJsonOffset = reader.ReadInt64();
				var depsJsonSize = reader.ReadInt64();
				var runtimeConfigJsonOffset = reader.ReadInt64();
				var runtimeConfigJsonSize = reader.ReadInt64();
				Flags = reader.ReadUInt64();
			}

			entryOffset = reader.Position;
		}

		/// <summary>
		/// Parses a bundle from the provided <see cref="IPEImage"/>
		/// </summary>
		/// <param name="peImage">The <see cref="IPEImage"/></param>
		/// <param name="moduleCreationOptions"></param>
		public static SingleFileBundle? FromPEImage(IPEImage peImage, ModuleCreationOptions moduleCreationOptions) {
			if (!IsBundle(peImage, out long bundleHeaderOffset))
				return null;
			return new SingleFileBundle(peImage.DataReaderFactory, (uint)bundleHeaderOffset, moduleCreationOptions);
		}

		/// <summary>
		/// Parses a bundle from the provided <see cref="IPEImage"/>
		/// </summary>
		/// <param name="peImage">The <see cref="IPEImage"/></param>
		/// /// <param name="headerOffset"></param>
		/// <param name="moduleCreationOptions"></param>
		public static SingleFileBundle FromPEImage(IPEImage peImage, long headerOffset, ModuleCreationOptions moduleCreationOptions) =>
			new SingleFileBundle(peImage.DataReaderFactory, (uint)headerOffset, moduleCreationOptions);

		/// <summary>
		/// Determines whether the provided <see cref="IPEImage"/> is a single file bundle.
		/// </summary>
		/// <param name="peImage">The <see cref="IPEImage"/></param>
		/// <param name="bundleHeaderOffset">The offset at which a bundle header was detected</param>
		public static bool IsBundle(IPEImage peImage, out long bundleHeaderOffset) {
			var reader = peImage.CreateReader();

			byte[] buffer = new byte[bundleSignature.Length];
			uint end = reader.Length - (uint)bundleSignature.Length;
			for (uint i = 0; i < end; i++) {
				reader.Position = i;
				byte b = reader.ReadByte();
				if (b != 0x8b)
					continue;
				buffer[0] = b;
				reader.ReadBytes(buffer, 1, bundleSignature.Length - 1);
				if (!buffer.SequenceEqual(bundleSignature))
					continue;
				reader.Position = i - sizeof(long);
				bundleHeaderOffset = reader.ReadInt64();
				if (bundleHeaderOffset <= 0 || bundleHeaderOffset >= reader.Length)
					continue;
				return true;
			}

			bundleHeaderOffset = 0;
			return false;
		}

		void InitializeBundleEntriesAndFolder() {
			var entries = ReadBundleEntries();

			var rootFolders = new LazyList<BundleFolder>(this);
			var rootEntries = new LazyList<BundleEntry>(this);

			var folders = new Dictionary<string, BundleFolder>();
			for (int i = 0; i < entries.Length; i++) {
				var entry = entries[i];
				var dirName = Path.GetDirectoryName(entry.RelativePath);

				if (string2.IsNullOrEmpty(dirName)) {
					rootEntries.Add(entry);
					continue;
				}

				GetFolder(dirName).Entries.Add(entry);
				continue;

				BundleFolder GetFolder(string directory) {
					if (folders.TryGetValue(directory, out var result))
						return result;
					result = folders[directory] = new BundleFolder(directory);
					var parentDir = Path.GetDirectoryName(directory);
					if (string2.IsNullOrEmpty(parentDir))
						rootFolders.Add(result);
					else
						GetFolder(parentDir).NestedFolders.Add(result);
					return result;
				}
			}

			Interlocked.CompareExchange(ref topLevelEntries, rootEntries, null);
			Interlocked.CompareExchange(ref topLevelFolders, rootFolders, null);
		}

		BundleEntry[] ReadBundleEntries() {
			var entries = new BundleEntry[originalEntryCount];

			var reader = dataReaderFactory.CreateReader();
			reader.Position = entryOffset;

			bool allowCompression = originalMajorVersion >= 6;

			for (int i = 0; i < originalEntryCount; i++) {
				long offset = reader.ReadInt64();
				long size = reader.ReadInt64();

				bool isCompressed = false;
				long decompressedSize = 0;
				if (allowCompression) {
					long compSize = reader.ReadInt64();
					if (compSize != 0) {
						decompressedSize = size;
						size = compSize;
						isCompressed = true;
					}
				}

				var type = (BundleEntryType)reader.ReadByte();
				string path = reader.ReadSerializedString();

				BundleEntry entry;
				switch (type) {
				case BundleEntryType.Unknown:
					entry = new UnknownBundleEntryMD(dataReaderFactory, (uint)offset, (uint)size, isCompressed, (uint)decompressedSize, path);
					break;
				case BundleEntryType.Assembly:
					entry = new AssemblyBundleEntryMD(dataReaderFactory, (uint)offset, (uint)size, isCompressed, (uint)decompressedSize, moduleCreationOptions, path);
					break;
				case BundleEntryType.NativeBinary:
					entry = new NativeBinaryBundleEntryMD(dataReaderFactory, (uint)offset, (uint)size, isCompressed, (uint)decompressedSize, path);
					break;
				case BundleEntryType.DepsJson:
				case BundleEntryType.RuntimeConfigJson:
					entry = new ConfigJSONBundleEntryMD(dataReaderFactory, (uint)offset, (uint)size, isCompressed, (uint)decompressedSize, type, path);
					break;
				case BundleEntryType.Symbols:
					entry = new SymbolBundleEntryMD(dataReaderFactory, (uint)offset, (uint)size, isCompressed, (uint)decompressedSize, path);
					break;
				default:
					throw new ArgumentOutOfRangeException();
				}

				entries[i] = entry;
			}

			return entries;
		}

		void IListListener<BundleEntry>.OnLazyAdd(int index, ref BundleEntry value) { }
		void IListListener<BundleEntry>.OnAdd(int index, BundleEntry value) => value.parentFolder = null;
		void IListListener<BundleEntry>.OnRemove(int index, BundleEntry value) { }
		void IListListener<BundleEntry>.OnResize(int index) { }
		void IListListener<BundleEntry>.OnClear() { }

		void IListListener<BundleFolder>.OnLazyAdd(int index, ref BundleFolder value) { }
		void IListListener<BundleFolder>.OnAdd(int index, BundleFolder value) => value.parentFolder = null;
		void IListListener<BundleFolder>.OnRemove(int index, BundleFolder value) { }
		void IListListener<BundleFolder>.OnResize(int index) { }
		void IListListener<BundleFolder>.OnClear() { }
	}
}

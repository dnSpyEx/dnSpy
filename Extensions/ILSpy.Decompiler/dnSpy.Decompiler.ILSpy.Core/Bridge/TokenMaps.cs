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
using dnlib.DotNet;
using WriterMetadata = dnlib.DotNet.Writer.Metadata;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Maps between the dnlib definitions of one module and the metadata tokens of the image that was handed to the
	/// upstream decompiler. Only definitions (types, fields, methods, params, properties, events, generic params) are
	/// mapped: everything the upstream decompiler references in another module resolves to a definition there.
	/// </summary>
	interface ITokenMap {
		/// <summary>
		/// Gets the token of <paramref name="definition"/> in the bridged image or 0 if it isn't part of it
		/// </summary>
		int GetToken(IMDTokenProvider definition);

		/// <summary>
		/// Gets the dnlib definition with token <paramref name="token"/> in the bridged image or null
		/// </summary>
		IMDTokenProvider? Resolve(int token);
	}

	/// <summary>
	/// Used when the upstream decompiler reads the module's original image: tokens are identical to dnlib's tokens.
	/// </summary>
	sealed class IdentityTokenMap : ITokenMap {
		readonly ModuleDef module;

		public IdentityTokenMap(ModuleDef module) => this.module = module ?? throw new ArgumentNullException(nameof(module));

		public int GetToken(IMDTokenProvider definition) {
			if (definition is null)
				throw new ArgumentNullException(nameof(definition));
			if (GetModule(definition) != module)
				return 0;
			// Members created in memory have rid 0 (or a rid that belongs to another row): they aren't in the image
			var token = definition.MDToken;
			if (token.Rid == 0 || !ReferenceEquals(module.ResolveToken(token.Raw), definition))
				return 0;
			return (int)token.Raw;
		}

		public IMDTokenProvider? Resolve(int token) {
			if (token == 0)
				return null;
			return module.ResolveToken(token);
		}

		static ModuleDef? GetModule(IMDTokenProvider definition) =>
			definition switch {
				IOwnerModule owner => owner.Module,
				ParamDef pd => pd.DeclaringMethod?.Module,
				_ => null,
			};
	}

	/// <summary>
	/// Used when dnlib rewrote (normalized) the module before handing it to the upstream decompiler. The tokens come
	/// from the dnlib writer, so new and edited members are mapped too.
	/// </summary>
	sealed class RewriteTokenMap : ITokenMap {
		readonly Dictionary<IMDTokenProvider, int> toToken;
		readonly Dictionary<int, IMDTokenProvider> fromToken;

		RewriteTokenMap(int capacity) {
			toToken = new Dictionary<IMDTokenProvider, int>(capacity);
			fromToken = new Dictionary<int, IMDTokenProvider>(capacity);
		}

		public int GetToken(IMDTokenProvider definition) {
			if (definition is null)
				throw new ArgumentNullException(nameof(definition));
			return toToken.TryGetValue(definition, out var token) ? token : 0;
		}

		public IMDTokenProvider? Resolve(int token) => fromToken.TryGetValue(token, out var def) ? def : null;

		public int Count => toToken.Count;

		public static RewriteTokenMap Create(ModuleDef module, WriterMetadata metadata) {
			var map = new RewriteTokenMap(1024);
			foreach (var type in module.GetTypes()) {
				map.Add(metadata, type);
				foreach (var gp in type.GenericParameters)
					map.Add(metadata, gp);
				foreach (var field in type.Fields)
					map.Add(metadata, field);
				foreach (var method in type.Methods) {
					map.Add(metadata, method);
					foreach (var pd in method.ParamDefs)
						map.Add(metadata, pd);
					foreach (var gp in method.GenericParameters)
						map.Add(metadata, gp);
				}
				foreach (var property in type.Properties)
					map.Add(metadata, property);
				foreach (var ev in type.Events)
					map.Add(metadata, ev);
			}
			return map;
		}

		void Add(WriterMetadata metadata, IMDTokenProvider definition) {
			// GetToken(object) doesn't support generic parameters
			var token = definition is GenericParam gp ? new MDToken(dnlib.DotNet.MD.Table.GenericParam, metadata.GetRid(gp)) : metadata.GetToken(definition);
			// Rid 0: the writer didn't write it (e.g. it reported an error to the no-throw logger)
			if (token.Rid == 0)
				return;
			int raw = (int)token.Raw;
			toToken[definition] = raw;
			fromToken[raw] = definition;
		}
	}
}

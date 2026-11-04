using System;
using System.Collections.Generic;
using System.Diagnostics;
using dnSpy.BamlDecompiler.Baml;

namespace dnSpy.BamlDecompiler {
	interface IHandler {
		BamlRecordType Type { get; }
		BamlElement Translate(XamlContext ctx, BamlNode node, BamlElement parent);
	}

	interface IDeferHandler {
		BamlElement TranslateDefer(XamlContext ctx, BamlNode node, BamlElement parent);
	}

	static class HandlerMap {
		static readonly Dictionary<BamlRecordType, IHandler> handlers;

		static HandlerMap() {
			handlers = new Dictionary<BamlRecordType, IHandler>();

			foreach (var type in GetTypes()) {
				if (type is not null && typeof(IHandler).IsAssignableFrom(type) &&
				    !type.IsInterface && !type.IsAbstract) {
					var handler = (IHandler)Activator.CreateInstance(type);
					handlers.Add(handler.Type, handler);
				}
			}
		}

		// The handlers don't need WPF, but some other types of this assembly do (e.g. the settings page): they can't be
		// loaded where WPF isn't available (dnSpy.Console, tests on Linux)
		static Type[] GetTypes() {
			try {
				return typeof(IHandler).Assembly.GetTypes();
			}
			catch (System.Reflection.ReflectionTypeLoadException ex) {
				return ex.Types;
			}
		}

		public static IHandler LookupHandler(BamlRecordType type) {
#if DEBUG
			switch (type) {
				case BamlRecordType.AssemblyInfo:
				case BamlRecordType.TypeInfo:
				case BamlRecordType.AttributeInfo:
				case BamlRecordType.StringInfo:
					break;
				default:
					if (!handlers.ContainsKey(type))
						throw new NotSupportedException(type.ToString());
					break;
			}
#endif
			return handlers.TryGetValue(type, out var handler) ? handler : null;
		}

		public static void ProcessChildren(XamlContext ctx, BamlBlockNode node, BamlElement nodeElem) {
			ctx.XmlNs.PushScope(nodeElem);
			nodeElem.Xaml.Element?.AddAnnotation(ctx.XmlNs.CurrentScope);
			foreach (var child in node.Children) {
				var handler = LookupHandler(child.Type);
				if (handler is null) {
					Debug.WriteLine("BAML Handler {0} not implemented.", child.Type);
					continue;
				}
				var elem = handler.Translate(ctx, child, nodeElem);
				if (elem is not null) {
					nodeElem.Children.Add(elem);
					elem.Parent = nodeElem;
				}

				ctx.CancellationToken.ThrowIfCancellationRequested();
			}
			ctx.XmlNs.PopScope();
		}
	}
}

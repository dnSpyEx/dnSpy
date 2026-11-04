#nullable enable
namespace WinFormsSample.Properties {
	using System;

	[global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "17.0.0.0")]
	[global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
	[global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
	internal class Resources {
		private static global::System.Resources.ResourceManager? resourceMan;

		internal Resources() {
		}

		[global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
		internal static global::System.Resources.ResourceManager ResourceManager {
			get {
				if (object.ReferenceEquals(resourceMan, null))
					resourceMan = new global::System.Resources.ResourceManager("WinFormsSample.Properties.Resources", typeof(Resources).Assembly);
				return resourceMan;
			}
		}

		internal static string Greeting => ResourceManager.GetString("Greeting")!;
	}
}

using System.Collections.Immutable;

namespace StandardLib;

public static class Names {
	public static string Join(ImmutableArray<string> names) => string.Join(", ", names) + ".";
}

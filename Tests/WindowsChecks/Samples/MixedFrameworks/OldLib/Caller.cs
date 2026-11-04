using NewLib;

namespace OldLib;

public static class Caller {
	public static string Greet(string name) => new Greeter().Hello(name) + "!";
}

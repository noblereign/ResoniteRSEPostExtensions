using System;
using System.Security.Cryptography;
using System.Text;

namespace RSEPostExtensions;
internal class Util {
	private static readonly string[] Adjectives = {
		"Swift", "Clever", "Brave", "Bright", "Calm", "Eager", "Kind", "Lively",
		"Silly", "Wild", "Gentle", "Fancy", "Proud", "Jolly", "Vast", "Sharp",
		"Creative", "Carefree", "Antsy", "Alert", "Tired", "Sneaky", "Crafty",
		"Poised", "Hungry", "Busy"
	};

	private static readonly string[] Colors = {
		"Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gold", "Silver",
		"Amber", "Ruby", "Neon", "Azure", "Jade", "Coral", "Indigo", "Onyx",
		"Plum", "Teal", "Violet", "Cyan", "Maroon", "Brown", "White", "Black", "Tan"
	};

	private static readonly string[] Critters = {
		"Badger", "Falcon", "Otter", "Panda", "Fox", "Wolf", "Eagle", "Tiger",
		"Dolphin", "Koala", "Lynx", "Owl", "Panther", "Rabbit", "Bear", "Hawk",
		"Beaver", "Aardvark", "Marten", "Dog", "Dragon", "Cat", "Avali", "Robot",
		"Beetle", "Expie", "Dinosaur", "Bird", "Snake", "Protogen"
	};

	public static string GetMemorableName(string input) {
		if (string.IsNullOrWhiteSpace(input))
			return "???";

		int seed = GetDeterministicHashCode(input);

		Random rand = new Random(seed);

		string adjective = Adjectives[rand.Next(Adjectives.Length)];
		string color = Colors[rand.Next(Colors.Length)];
		string animal = Critters[rand.Next(Critters.Length)];

		return $"{adjective}{color}{animal}";
	}

	private static int GetDeterministicHashCode(string str) {
		using (SHA256 sha256 = SHA256.Create()) {
			byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(str));
			return BitConverter.ToInt32(hashBytes, 0);
		}
	}
}

namespace AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;

/// <summary>
/// Publicly known UnityCN keys, tried automatically on encrypted bundles.
/// </summary>
/// <remarks>
/// List from AnimeStudio (<see href="https://github.com/Escartem/AnimeStudio"/>, MIT License,
/// Copyright (c) 2016 Radu, 2016-2020 Perfare, 2022-2024 Razmoth).
/// </remarks>
public static class UnityCnKnownKeys
{
	public static IReadOnlyList<(string Game, string Key)> Entries { get; } =
	[
		("Punishing Gray Raven - Global/Korea", "6B75726F6B75726F6B75726F6B75726F"),
		("Punishing Gray Raven - China/Japan/Taiwan", "587865636F6472506547616B61326536"),
		("Punishing Gray Raven - China/Japan/Taiwan (old)", "7935585076714C4F72436F6B57524961"),
		("Archeland / Kalpa of Universe", "426C61636B4A61636B50726F6A656374"),
		("Archeland 1.1.14", "50726F6A65637441726368654C616E64"),
		("Neural Cloud", "31636162383436663532393031633965"),
		("Neural Cloud CN", "62363238363766353164326561376266"),
		("Higan: Eruthyll", "45317832633361346C35693662377572"),
		("White Chord", "79756C6F6E6731383638676E6F6C7579"),
		("Mecharashi", "33384338334631333245374637413041"),
		("Castlevania: Moon Night Fantasy", "31323334353637383132333435363738"),
		("Hua Yi Shan Xin Zhi Yue", "494E484A6E68647970716B3534377864"),
		("Doula Continent", "52346366773339474644326661785756"),
		("Bless Global", "6C6F6E67747567616D652E796A66623F"),
		("Starside", "41394A3542384D4A50554D3539464B57"),
		("Resonance Solstice", "5265736F6E616E63655265626F726E52"),
		("Oblivion Override", "7179666D6F6F6E323331323433343532"),
		("Dawnlands", "636F6465737339353237636F64657373"),
		("BB", "5F6C4E3F3A3F233F3F3F3F663F1A3F3F"),
		("Dynasty Legends 2", "746169686567616D6573323032323032"),
		("Evernight CN", "68687878747478736868787874747873"),
		("Xintianlong Babu", "61323562623133346363326464333265"),
		("Frostpunk: Beyond the Ice", "7368756978696E673838383838383838"),
		("Cat Fantasy", "43614461566637323538576877363433"),
		("Azur Promilia CBT2", "7a346c32336268352333356826333231"),
	];

	/// <summary>
	/// The known keys as candidates.
	/// </summary>
	public static IReadOnlyList<UnityCnKeyCandidate> Candidates { get; } = Entries
		.Select(e => new UnityCnKeyCandidate(Convert.FromHexString(e.Key), e.Key, "hex", $"known key: {e.Game}"))
		.ToArray();
}

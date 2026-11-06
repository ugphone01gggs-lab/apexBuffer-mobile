// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Rulesets.Osu.Mods
{
    public class OsuModApexTest : Mod
    {
        public override string Name => "ApexBuffer Test";
        public override string Acronym => "AX";
        public override LocalisableString Description => "Test mod. Does nothing. Used to confirm custom mods load.";
        public override ModType Type => ModType.Fun;
        public override bool Ranked => false;
    }
}

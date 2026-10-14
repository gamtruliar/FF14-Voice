using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FF14Sound.Voices;

public static class VoiceTraitsDesigner
{
    private static bool Has(string name, string pattern) => Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Apply(NpcVoiceEntry row)
    {
        var name = string.Join(" / ", row.Names);
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(VoiceResolver.Normalize(row.English.Length > 0 ? row.English : row.Name)));
        var human = row.Appearances.Where(a => a.Race is >= 1 and <= 8 && a.Gender <= 1 && a.BodyType is 1 or 3 or 4 && a.Tribe is >= 1 and <= 16).ToArray();
        // A name group can contain many different people. Never call a majority vote a fact.
        var complete = human.Length > 0 && human.Length == row.NpcIds.Length && human.Select(a => a.NpcId).Order().SequenceEqual(row.NpcIds.Order());
        var genders = human.Select(a => a.Gender).Distinct().ToArray();
        var bodies = human.Select(a => a.BodyType).Distinct().ToArray();
        var female = Has(name, @"\b(woman|women|girl|female|lady|mother|grandmother|sister|daughter|waitress|actress|priestess|huntress|maid|noblewoman|townswoman)\b|女性|女の子|少女|老婆|老女|婦人|おばあ|娘|侍女");
        var male = Has(name, @"\b(man|men|boy|male|father|grandfather|brother|son|waiter|butler|nobleman|townsman)\b|男性|男の子|少年|老爺|おじい|息子");
        string genderBasis;
        if (complete && genders.Length == 1)
        {
            row.VoiceGender = genders[0] == 0 ? "男聲" : "女聲";
            genderBasis = $"性別聲線參照 client ENpcBase.Gender={genders[0]}（全組 {human.Length} 個 NPC ID 一致）";
        }
        else if (female != male)
        {
            row.VoiceGender = female ? "女聲" : "男聲";
            genderBasis = "性別聲線參照名稱中明確男／女稱謂";
        }
        else
        {
            row.VoiceGender = seed[5] % 2 == 0 ? "男聲" : "女聲";
            genderBasis = (genders.Length > 1 ? "同名 NPC 有不同性別；" : "性別資料未完整確認；") + "男／女聲為固定創作選角";
        }

        var old = Has(name, @"\b(elder|elderly|old man|old woman|grandmother|grandfather|grandpa|grandma|aged)\b|老人|老女|老爺|老婆|おじい|おばあ|年老いた");
        var young = Has(name, @"\b(child|children|boy|girl|urchin|schoolboy|schoolgirl|little lad|little lass)\b|子供|子ども|少年|少女|幼い|男の子|女の子");
        string ageBasis;
        if (old != young)
        {
            row.VoiceAge = old ? "老年" : "幼年／少年";
            ageBasis = "年齡感參照名稱中老／幼稱謂";
        }
        else if (complete && bodies.Length == 1 && bodies[0] is 3 or 4)
        {
            row.VoiceAge = bodies[0] == 3 ? "老年" : "幼年／少年";
            ageBasis = $"年齡感參照 ENpcBase.BodyType={bodies[0]} 嘅老年／年幼模型；非實際歲數";
        }
        else
        {
            // BodyType=1 means normal, not an exact age. Do not turn Lalafell height into childhood.
            row.VoiceAge = "成年";
            ageBasis = complete && bodies.Length == 1 && bodies[0] == 1
                ? "一般模型 BodyType=1，採用成年演繹；未確認實際歲數"
                : "年齡資料缺失或同名模型有差異，暫採成年演繹";
        }
        row.VoiceTraitsBasis = genderBasis + "；" + ageBasis;
        row.VoiceDescription = $"{row.VoiceAge}{row.VoiceGender}。" + row.VoiceDescription;
    }
}

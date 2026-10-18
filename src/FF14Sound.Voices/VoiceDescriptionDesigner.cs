using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FF14Sound.Voices;

/// <summary>Voice direction, not a claim about official casting or the sound of an actor.</summary>
public static class VoiceDescriptionDesigner
{
    public const int Version = 4;
    private const string Wiki = "https://ffxiv.consolegameswiki.com/wiki/";
    private const string Guild = Wiki + "Guildmaster";
    private const string Inspector = "https://na.finalfantasyxiv.com/lodestone/topics/detail/3cfad29b51fcad14f3665c54b843d5232ab63217";
    private sealed record Known(string Aliases, string Identity, string Direction, string Source);
    // Identities are sourced; every acoustic/performance direction below is authored for this app.
    private static readonly Known[] KnownCharacters =
    [
        new("Mother Miounne|Miounne|ミューヌ", "格里達尼亞 Carline Canopy 店主", "溫潤、成熟嘅中音，像照顧新旅人嘅店主；語速從容，勸告時柔和而篤定，句尾輕輕收住。", Wiki + "Miounne"),
        new("Baderon|Baderon Tenfingers|バデロン", "Drowning Wench 店主、前傭兵", "厚實偏低、略帶沙啞，豪爽而親切；說話像酒館裏分享經驗，短句有力，提點新人時放柔，唔刻意大叫。", Wiki + "Baderon"),
        new("Momodi|Momodi Modi|モモディ", "烏爾達哈 Quicksand 店主", "明亮爽利嘅中高音，健談而老練；語速稍快，重點咬字清楚，帶笑意地招呼旅人，勸告時稍微壓低句尾。", Wiki + "Momodi"),
        new("Hildibrand|Hildibrand Manderville|ヒルディブランド", "調查員、名偵探", "洪亮而有彈性嘅中低音，紳士式自信，宣告推理前留戲劇停頓；禮貌用語抑揚分明，驚訝時節奏突然加快，帶喜劇感。", Inspector),
        new("Nashu Mhakaracca|Nashu|ナシュ・マカラッカ|ナシュ", "Hildibrand 嘅偵探助手", "輕盈明亮嘅中高音，熱心又充滿好奇；語速輕快，疑問句自然上揚，發現線索時興奮但咬字仍清楚。", Inspector),
        new("Godbert|Godbert Manderville|ゴッドベルト", "金工師、Manderville & Manderville 經營者", "渾厚圓潤嘅低音，氣息充足，從容有禮；招呼人時溫暖，宣佈決定時胸腔共鳴加強，豪邁而唔兇惡。", Wiki + "Godbert"),
        new("Gerolt|ゲロルト", "古武器任務相關武器工匠", "粗礪帶砂感嘅低音，務實直率；短句稍重，像一邊做工一邊回話，講製作細節時專注，保留清晰字頭。", Wiki + "Rowena"),
        new("Rowena|ロウェナ", "古物商人、Rowena's House of Splendors 主事者", "成熟、略低而俐落嘅中音，自信精明；交易條件逐點講清楚，重要字加重，句尾乾淨，笑意收斂。", Wiki + "Rowena"),
        new("Khloe Aliapoh|クロ・アリアポー", "Wondrous Tails 相關角色", "清脆輕柔嘅高音，帶童真同好奇心；聽冒險故事般雀躍，語速稍快，驚喜時上揚，避免刺耳尖叫。", Wiki + "Khloe_Aliapoh"),
        new("Jandelaine|ジャンドゥレーヌ", "美容師", "清亮而有舞台感嘅中音，咬字講究；讚美時拉開抑揚，句子有華麗停頓，熱情自信但唔用唱腔。", Wiki + "Jandelaine"),
        new("Beatin|ベアティヌ", "木工師行會導師", "偏低、乾淨而克制嘅中音，語速略慢；談工藝細節時重音精準，帶專注嘅熱情，句尾留短停頓。", Guild),
        new("Geva|ゲヴァ", "皮革工行會導師", "結實俐落嘅中音，要求嚴謹；指示短而清楚，重點稍重，評價作品時節奏有分寸，避免拖尾。", Guild),
        new("Fufucha|フフチャ", "園藝師行會導師", "柔亮而溫暖嘅中高音，耐心細緻；自然換氣，語速舒緩，講解採集時帶鼓勵，感嘆輕輕落下。", Guild),
        new("Ywain|イウェイン", "槍術師行會導師", "沉著堅實嘅中低音，教學口吻有紀律；語速適中，關鍵指令重讀，段落間停頓明確，收尾篤定。", Guild),
        new("Luciane|ルシアヌ", "弓術師行會導師", "明淨穩定嘅中音，冷靜而細心；講解時每個重點分開，鼓勵時稍放柔，尾音短而平穩。", Guild),
        new("Hamon|Hamon Holyfist|ハモン", "格鬥家行會導師", "帶少許乾啞嘅中低音，教拳時節拍鮮明；日常說話帶幽默，認真時氣息變扎實，保留自然停頓。", Guild),
        new("Mylla|ミラ", "劍術師行會導師", "清晰有力嘅中音，爽快而可靠；命令句節奏緊湊，提點時平穩，句尾乾脆，唔持續提高音量。", Guild),
        new("Wyrnzoen|ヴィルンズーン", "斧術師行會導師", "厚重低音，氣息寬闊，帶教練式力度；短句分明，重讀動作指令，平常交流收斂音量。", Guild),
        new("Cocobuki|ココブキ", "咒術師行會導師", "收斂、偏暗嘅中音，語速慢半拍；術語讀得仔細，句間稍作停頓，神秘感來自平靜語調，唔刻意怪笑。", Guild),
        new("E-Sumi-Yan|エ・スミ・ヤン", "幻術師行會導師", "清澈柔和嘅中高音，氣息穩定，帶靜心教導嘅安定感；語速慢而連貫，字尾輕收，避免空泛氣聲。", Guild),
        new("Thubyrgeim|トゥビルゲイム", "秘術師行會教導者", "穩健清晰嘅中音，理性而耐心；解說按步驟分段，數字術語清楚，糾正錯誤時堅定但平和。", Guild),
        new("Jacke|ジャック", "雙劍師行會導師", "靈活略沙嘅中音，閒談輕鬆，帶機敏笑意；語速稍快，重要提醒放慢加重，收尾短促。", Guild),
        new("Severian|セヴェリアン", "鍊金術師行會導師", "偏乾、略低嘅中音，專注而精密；術語快速但清楚，思考時短停頓，結論突然加重，情緒收斂。", Guild),
        new("Serendipity|セレンディピティー", "金工師行會導師", "明亮圓潤嘅中高音，講解細緻而有熱誠；語速輕快，描述精巧作品時柔和，發現問題時停一拍再說。", Guild),
        new("Redolent Rose|レドレント・ローズ", "裁衣匠行會導師", "圓潤有共鳴嘅中低音，措辭從容而講究；評述設計時抑揚細膩，指導時耐心，尾音有分寸地延展。", Guild),
        new("H'naanza|ハ・ナンザ", "甲冑師行會導師", "堅實偏低嘅中音，務實沉著；步驟指令節奏明確，評價工藝時重點分明，語氣有份量而唔粗暴。", Guild),
        new("Brithael|ブリサエル", "鍛鐵匠行會導師", "厚實帶輕微砂感嘅中低音，爽直隨和；講解像工場閒談，句首有力，句尾放鬆，專業詞彙清楚。", Guild),
        new("Lyngsath|リングサス", "烹調師行會導師", "寬厚溫暖嘅中低音，招呼熱情；像廚房裏交代工序，語速俐落，講食物時語調稍揚，停頓自然。", Guild),
        new("Adalberta|アダルベルタ", "採礦師行會導師", "結實爽朗嘅中音，踏實而有活力；咬字乾淨，提醒危險時放慢，鼓勵時帶笑意，節奏穩定。", Guild),
        new("Wawalago|ワワラゴ", "漁師行會導師", "略帶沙感嘅中低音，隨性健談；句子像說釣魚見聞，停頓悠閒，提到關鍵技巧時突然清晰加重。", Guild),
        new("Sisipu|シシプ", "漁師行會教導者", "清脆俐落嘅中高音，教學認真；節奏明快，提醒事項一字一字講清楚，帶耐心而唔過分甜膩。", Guild)
    ];
    private static readonly Dictionary<string, Known> KnownByName = KnownCharacters
        .SelectMany(k => k.Aliases.Split('|').Select(n => (Name: VoiceResolver.Normalize(n), Value: k)))
        .ToDictionary(p => p.Name, p => p.Value);

    private sealed record Rule(string Label, string Pattern, string Direction)
    {
        public Regex Matcher { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
    // Specific roles precede broad ones; English uses word boundaries to avoid names such as "Edgard".
    private static readonly Rule[] Roles =
    [
        new("活動／接待", @"\b(gate keeper|guide|attendant|receptionist|usher)\b|案内|受付|窓口", "明亮清楚嘅中音，親切有禮，像活動接待員；語速明快，規則逐點講清，邀請時句尾自然上揚。"),
        new("醫療／照護", @"\b(healer|chirurgeon|physician|nurse|medic)\b|治療師|医師|看護|衛生兵", "柔和穩定嘅中音，安撫而專注；語速稍慢，指示清楚，提醒時輕輕加重，呼吸平穩。"),
        new("兒童", @"\b(child|children|boy|girl|urchin|schoolboy|schoolgirl)\b|子供|少年|少女|幼い", "清亮輕巧嘅高音，帶童真；短句輕快、好奇自然，情緒跟隨對白，避免尖叫同刻意娃娃腔。"),
        new("長者", @"\b(elder|elderly|old man|old woman|grandmother|grandfather)\b|老人|老女|老爺", "柔厚帶少許乾啞嘅中低音，像長者分享經驗；语速從容，換氣自然，停頓有耐心，字音仍清楚。"),
        new("軍官／領隊", @"\b(captain|commander|officer|sergeant|marshal|lieutenant)\b|隊長|将校|司令", "堅實沉著嘅中低音，帶指揮者份量；語速適中，命令短而明確，重要人名地點加重，尾音乾脆。"),
        new("守衛／士兵", @"\b(guard|soldier|sentry|sentinel|wailer|trooper|infantry)\b|衛兵|兵士|騎兵|守衛|警備", "結實俐落嘅中音，警覺而守紀律；報告平穩，提醒時重音清楚，短句收得乾脆。"),
        new("騎士", @"\b(knight|templar|paladin)\b|騎士", "圓厚有力嘅中低音，莊重克制；禮節用語清晰，宣誓時放慢，命令時乾脆，維持穩定音量。"),
        new("學者／研究", @"\b(scholar|researcher|professor|historian|archaeologist|librarian|scribe)\b|研究|学者|司書|書記", "乾淨內斂嘅中音，理性而專注；術語咬字精確，解釋時分段停頓，結論輕輕加重。"),
        new("教師", @"\b(teacher|tutor|instructor|mentor)\b|教師|教官|先生", "清晰穩定嘅中音，耐心教導；重點前短停頓，步驟逐一講解，鼓勵柔和而糾正堅定。"),
        new("學徒／學生", @"\b(apprentice|student|trainee|novice|recruit)\b|見習|学生|新兵", "清亮略輕嘅中音，認真而帶求知感；語速稍快，疑問句略上揚，回答前可有短暫猶豫。"),
        new("工匠／修理", @"\b(smith|blacksmith|armorer|armourer|artisan|craftsman|carpenter|mechanic)\b|鍛冶|職人|大工|修理", "結實帶少許砂感嘅中低音，樸實直接；工序說明清楚，節奏像熟練工作，句尾平穩。"),
        new("工程／技術", @"\b(engineer|technician|machinist)\b|技師|技術者|機工", "清晰緊實嘅中音，務實專注；數字同零件名稱讀準，步驟之間稍停，問題排解時語速穩定。"),
        new("裁衣／珠寶", @"\b(weaver|tailor|goldsmith|jeweler|jeweller|leatherworker)\b|裁縫|彫金|革細工", "圓潤細膩嘅中音，講究但親切；描述細節時放慢，評價時抑揚柔和，咬字精巧乾淨。"),
        new("鍊金／藥劑", @"\b(alchemist|apothecary|herbalist)\b|錬金|薬師|薬剤", "偏乾而清楚嘅中音，謹慎專注；配方術語讀清，說明用量時放慢，結論短而確定。"),
        new("料理／餐飲", @"\b(cook|chef|culinarian|baker|waiter|waitress)\b|料理|調理|給仕", "溫暖爽朗嘅中音，招呼熱情；語速輕快，介紹食物時帶笑意，交代步驟乾脆。"),
        new("酒館／旅店", @"\b(innkeeper|tavernkeep|tavernkeeper|barkeep|bartender)\b|酒場|宿屋|宿の", "厚暖放鬆嘅中音，像招呼熟客；節奏從容，分享消息時輕輕加重，句尾帶親切笑意。"),
        new("商販", @"\b(merchant|vendor|trader|shopkeeper|salesman|saleswoman|monger)\b|商人|商売|売り|販売", "明亮爽快嘅中音，健談而務實；貨品價格講得清楚，招呼有活力，成交時乾淨收尾。"),
        new("採礦／勞動", @"\b(miner|quarryman|laborer|labourer|worker|porter)\b|鉱夫|採掘|労働|人夫|運搬", "厚實略粗嘅中低音，樸直自然；短句有力，語速適中，字尾放鬆，避免一直喘氣。"),
        new("園藝／農牧", @"\b(botanist|gardener|farmer|shepherd|rancher|herder)\b|園芸|農夫|農婦|牧童|牧場", "溫和樸實嘅中音，帶生活感；語速悠然，描述作物同天氣時自然起伏，招呼親切。"),
        new("漁師", @"\b(fisher|fisherman|fisherwoman|angler)\b|漁師|釣り", "略帶砂感嘅中音，閒適直接；像岸邊分享見聞，短句自然，講技巧時咬字更清楚。"),
        new("水手／船員", @"\b(sailor|seaman|deckhand|boatman|ferryman|pirate)\b|船員|船乗り|水夫|海賊|渡し守", "厚實開闊嘅中低音，爽朗直接；報方位時字頭有力，閒談放鬆，節奏像甲板上交談。"),
        new("旅行／冒險", @"\b(adventurer|traveler|traveller|wanderer|pilgrim|explorer)\b|冒険者|旅人|巡礼|探検", "自然明亮嘅中音，帶好奇與活力；分享見聞時節奏有起伏，請託時誠懇，語速適中。"),
        new("獵人／斥候", @"\b(hunter|huntress|ranger|scout|tracker)\b|狩人|猟師|斥候", "收斂清晰嘅中低音，警覺專注；語速平穩，方向同警告詞稍重，句尾短，音量唔過高。"),
        new("僧侶／神職", @"\b(priest|priestess|acolyte|monk|nun|cleric)\b|神官|司祭|僧侶|修道", "柔厚安定嘅中音，莊重而有慈意；语速舒緩，段落停頓完整，祈願用語輕柔連貫。"),
        new("貴族／官員", @"\b(noble|nobleman|noblewoman|aristocrat|ambassador|diplomat|magistrate)\b|貴族|領主|大使|使節", "圓潤克制嘅中低音，措辭端正；節奏從容，重點有分寸地加重，尾音穩定，帶正式場合感。"),
        new("樂師／表演者", @"\b(bard|minstrel|musician|singer|dancer|performer|actor|actress)\b|吟遊|詩人|歌手|踊り|楽師|芸人", "明亮有彈性嘅中音，抑揚豐富；介紹時有舞台感，日常對白仍自然，唔把台詞唱出來。"),
        new("信差／傳令", @"\b(messenger|courier|postman|postmoogle|herald)\b|配達|伝令|使者", "清楚俐落嘅中音，稍快但唔急亂；人名地點重讀，傳話逐句分明，報告完乾脆收尾。"),
        new("難民／流亡", @"\b(refugee|displaced|exile)\b|難民|避難民|流民", "柔而略乾嘅中音，保留疲憊感；語速稍慢，請託誠懇，提到希望時稍稍明亮，唔過度哭腔。"),
        new("市民／村民", @"\b(citizen|villager|resident|townsman|townswoman)\b|市民|村人|住民|町人", "自然樸實嘅中音，像街坊交談；語速適中，情绪跟台詞變化，停頓鬆弛而咬字清楚。"),
        new("盜賊／走私", @"\b(thief|bandit|brigand|smuggler|rogue)\b|盗賊|盗人|密輸", "略沙、收斂嘅中低音，機敏而警惕；短句稍快，重點低聲加重，語尾乾淨，避免怪腔。"),
        new("僕役／助手", @"\b(servant|butler|maid|assistant|retainer|steward)\b|従者|執事|侍女|助手|使用人", "柔和清楚嘅中音，恭敬而俐落；回覆迅速，說明完整，音量適中，尾音穩定。"),
        new("莫古利", @"\bmoogle\b|モーグリ", "輕巧圓潤嘅高音，跳躍而親切；節奏活潑，尾音柔軟，唔額外加口頭禪，保持台詞清晰。"),
        new("機械／系統", @"\b(automaton|terminal|system|robot|node)\b|端末|自動人形|システム", "清澈均勻嘅中音，情緒克制；字音長度穩定，資訊分段明確，通知句尾平直，避免額外音效。")
    ];
    private static readonly Rule[] Moods =
    [
        new("受傷", @"\b(wounded|injured|hurt)\b|負傷|傷ついた", "帶少許吃力同疲憊，縮短句子、換氣稍多，但唔添加呻吟。"),
        new("擔憂", @"\b(worried|anxious|concerned|troubled|nervous)\b|心配|不安|困った", "語氣帶擔憂，句中稍有猶豫，重點清楚，唔過度顫抖。"),
        new("憤怒", @"\b(angry|irate|furious|indignant)\b|怒れる|怒った|憤慨", "字頭稍重，節奏繃緊，保留不滿但唔持續喊叫。"),
        new("疲倦", @"\b(weary|tired|exhausted|sleepy)\b|疲れた|眠そう", "語速略慢、力度稍輕，停頓帶疲倦感，仍然清晰可懂。"),
        new("歡欣", @"\b(cheerful|joyful|jubilant|excited|eager)\b|陽気|喜ぶ|嬉しそう", "帶笑意同期待，抑揚活潑，關鍵詞稍揚，保持自然音量。"),
        new("膽怯", @"\b(timid|shy|frightened|scared|fearful)\b|臆病|怯え|内気", "起句力度輕，停頓略猶豫，音量收斂，唔把整句讀成耳語。"),
        new("悲傷", @"\b(grieving|mourning|sorrowful|tearful)\b|悲しむ|悲しげ|泣いて", "音色稍暗，語速放慢，情緒壓住，句尾輕落，唔添加哭聲。"),
        new("醉意", @"\b(drunken|drunk|tipsy)\b|酔った|酔いどれ", "節奏略鬆散，帶少許醉意，字音保持可辨，唔添加打嗝。")
    ];
    private static readonly string[] Timbres = ["溫潤圓滑", "清亮通透", "厚實柔和", "略帶砂感", "乾淨緊實", "輕盈柔亮", "飽滿有共鳴", "偏乾而清楚", "柔厚內斂", "明亮有彈性", "細膩平滑", "清澈安定"];
    private static readonly string[] Pitches = ["中低音", "自然中音", "偏亮嘅中音", "柔和中高音", "偏沉嘅中音", "圓潤低中音"];
    private static readonly string[] Rhythms = ["語速適中，停頓從容", "語速輕快，換氣俐落", "語速稍慢，每句分段清楚", "短句輕快，長句稍放慢", "語速平穩，重點前留短停頓", "節奏輕盈，句間自然換氣", "語速鬆弛，關鍵詞稍放慢", "語速適中，句首稍有力度"];
    private static readonly string[] Attitudes = ["像可靠旅伴般親切", "帶沉著、內斂嘅自信", "帶輕微笑意同好奇", "坦率隨和，像面對面閒談", "溫和有禮，帶細心聆聽感", "精神爽利，表達簡潔", "謹慎而誠懇，情緒含蓄", "放鬆自然，敘述有生活感"];
    private static readonly string[] Endings = ["句尾柔和落下", "重點咬字清楚，尾音乾淨", "疑問句輕揚，敘述句平穩", "重音集中喺關鍵詞", "尾音輕收，唔刻意拉長", "情緒變化細緻，音量穩定", "字頭清楚，連句流暢", "轉折前稍停，語氣自然延續"];

    public static bool IsGenerated(NpcVoiceEntry row) => row.DescriptionVersion > 0 || row.DescriptionBasis.StartsWith("建議演繹（按名稱／通用模板）", StringComparison.Ordinal);

    public static void Apply(NpcVoiceEntry row)
    {
        row.DescriptionVersion = Version;
        row.DescriptionSource = "";
        if (row.Names.Any(CollectiveSpeaker.IsCollective))
        {
            row.VoiceDescription = CollectiveSpeaker.Description;
            row.DescriptionBasis = "群體講者名稱；多人齊聲演繹";
            row.VoiceGender = "多人聲";
            row.VoiceAge = "";
            row.VoiceTraitsBasis = "群體講者，不分配單一人物性別或年齡";
            return;
        }
        var known = row.Names.Select(VoiceResolver.Normalize).Where(KnownByName.ContainsKey).Select(n => KnownByName[n]).Distinct().ToArray();
        if (known.Length == 1)
        {
            row.VoiceDescription = known[0].Direction;
            row.DescriptionBasis = "已知身份：" + known[0].Identity + "；聲線為創作演繹，非官方音色";
            row.DescriptionSource = known[0].Source;
            VoiceTraitsDesigner.Apply(row);
            return;
        }
        var name = string.Join(" / ", row.Names);
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(VoiceResolver.Normalize(row.English.Length > 0 ? row.English : row.Name)));
        var role = Roles.FirstOrDefault(r => r.Matcher.IsMatch(name));
        var mood = Moods.FirstOrDefault(r => r.Matcher.IsMatch(name));
        row.VoiceDescription = role?.Direction ?? $"{Timbres[seed[0] % Timbres.Length]}嘅{Pitches[seed[1] % Pitches.Length]}，{Rhythms[seed[2] % Rhythms.Length]}；{Attitudes[seed[3] % Attitudes.Length]}，{Endings[seed[4] % Endings.Length]}。";
        if (mood is not null) row.VoiceDescription += mood.Direction;
        else if (role is not null) row.VoiceDescription += Endings[seed[4] % Endings.Length] + "。";
        row.DescriptionBasis = role is not null
            ? "名稱職務線索：" + role.Label + (mood is null ? "" : "／" + mood.Label) + "；創作演繹，非官方音色"
            : "按名字分配嘅創作聲線；身份未確認，非官方音色";
        if (role is null && mood is not null) row.DescriptionBasis += "；名稱情緒線索：" + mood.Label;
        VoiceTraitsDesigner.Apply(row);
    }
}

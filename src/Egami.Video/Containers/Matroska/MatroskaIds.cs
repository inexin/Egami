namespace Egami.Video.Containers.Matroska;

/// <summary>EBML and Matroska element ids (RFC 8794, RFC 9559), with their length markers.</summary>
internal static class MatroskaIds
{
    public const uint Ebml = 0x1A45DFA3;
    public const uint EbmlVersion = 0x4286;
    public const uint EbmlReadVersion = 0x42F7;
    public const uint EbmlMaxIdLength = 0x42F2;
    public const uint EbmlMaxSizeLength = 0x42F3;
    public const uint DocType = 0x4282;
    public const uint DocTypeVersion = 0x4287;
    public const uint DocTypeReadVersion = 0x4285;
    public const uint Void = 0xEC;
    public const uint Crc32 = 0xBF;

    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Seek = 0x4DBB;
    public const uint SeekId = 0x53AB;
    public const uint SeekPosition = 0x53AC;

    public const uint Info = 0x1549A966;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint Title = 0x7BA9;
    public const uint MuxingApp = 0x4D80;
    public const uint WritingApp = 0x5741;
    public const uint DateUtc = 0x4461;

    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackUid = 0x73C5;
    public const uint TrackType = 0x83;
    public const uint FlagEnabled = 0xB9;
    public const uint FlagDefault = 0x88;
    public const uint FlagLacing = 0x9C;
    public const uint MaxBlockAdditionId = 0x55EE;
    public const uint DefaultDuration = 0x23E383;
    public const uint Name = 0x536E;
    public const uint Language = 0x22B59C;
    public const uint LanguageBcp47 = 0x22B59D;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint CodecDelay = 0x56AA;
    public const uint SeekPreRoll = 0x56BB;
    public const uint ContentEncodings = 0x6D80;
    public const uint ContentEncoding = 0x6240;
    public const uint ContentEncodingScope = 0x5032;
    public const uint ContentEncodingType = 0x5033;
    public const uint ContentCompression = 0x5034;
    public const uint ContentCompAlgo = 0x4254;
    public const uint ContentCompSettings = 0x4255;

    public const uint Video = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint DisplayWidth = 0x54B0;
    public const uint DisplayHeight = 0x54BA;
    public const uint DisplayUnit = 0x54B2;
    public const uint FlagInterlaced = 0x9A;
    public const uint FieldOrder = 0x9D;
    public const uint ColourSpace = 0x2EB524;
    public const uint Colour = 0x55B0;
    public const uint MatrixCoefficients = 0x55B1;
    public const uint Range = 0x55B9;
    public const uint TransferCharacteristics = 0x55BA;
    public const uint Primaries = 0x55BB;
    public const uint BitsPerChannel = 0x55B2;
    public const uint ChromaSubsamplingHorz = 0x55B3;
    public const uint ChromaSubsamplingVert = 0x55B4;
    public const uint ChromaSitingHorz = 0x55B7;
    public const uint ChromaSitingVert = 0x55B8;
    public const uint MaxCll = 0x55BC;
    public const uint MaxFall = 0x55BD;
    public const uint MasteringMetadata = 0x55D0;
    public const uint PrimaryRChromaticityX = 0x55D1;
    public const uint PrimaryRChromaticityY = 0x55D2;
    public const uint PrimaryGChromaticityX = 0x55D3;
    public const uint PrimaryGChromaticityY = 0x55D4;
    public const uint PrimaryBChromaticityX = 0x55D5;
    public const uint PrimaryBChromaticityY = 0x55D6;
    public const uint WhitePointChromaticityX = 0x55D7;
    public const uint WhitePointChromaticityY = 0x55D8;
    public const uint LuminanceMax = 0x55D9;
    public const uint LuminanceMin = 0x55DA;
    public const uint AlphaMode = 0x53C0;

    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;

    public const uint Cluster = 0x1F43B675;
    public const uint Timestamp = 0xE7;
    public const uint Position = 0xA7;
    public const uint PrevSize = 0xAB;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockDuration = 0x9B;
    public const uint ReferenceBlock = 0xFB;
    public const uint DiscardPadding = 0x75A2;
    public const uint BlockAdditions = 0x75A1;
    public const uint BlockMore = 0xA6;
    public const uint BlockAddId = 0xEE;
    public const uint BlockAdditional = 0xA5;

    public const uint Cues = 0x1C53BB6B;
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
    public const uint CueRelativePosition = 0xF0;

    public const uint Tags = 0x1254C367;
    public const uint Tag = 0x7373;
    public const uint Targets = 0x63C0;
    public const uint TagTrackUid = 0x63C5;
    public const uint SimpleTag = 0x67C8;
    public const uint TagName = 0x45A3;
    public const uint TagString = 0x4487;

    public const uint Chapters = 0x1043A770;
    public const uint EditionEntry = 0x45B9;
    public const uint ChapterAtom = 0xB6;
    public const uint ChapterUid = 0x73C4;
    public const uint ChapterTimeStart = 0x91;
    public const uint ChapterTimeEnd = 0x92;
    public const uint ChapterDisplay = 0x80;
    public const uint ChapString = 0x85;
    public const uint ChapLanguage = 0x437C;
    public const uint ChapLanguageBcp47 = 0x437D;

    public const uint Attachments = 0x1941A469;
    public const uint AttachedFile = 0x61A7;
    public const uint FileDescription = 0x467E;
    public const uint FileName = 0x466E;
    public const uint FileMediaType = 0x4660;
    public const uint FileData = 0x465C;
    public const uint FileUid = 0x46AE;

    /// <summary>Whether an id is a top-level Segment child, which ends an unknown-size Cluster.</summary>
    public static bool IsTopLevel(uint id) =>
        id is Cluster or Cues or Tags or Chapters or Attachments or SeekHead or Info or Tracks or Segment or Ebml;
}

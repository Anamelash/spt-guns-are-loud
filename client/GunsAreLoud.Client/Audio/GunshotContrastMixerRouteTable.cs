using System;

namespace GunsAreLoud.Client.Audio
{
    // Shared by the runtime and the Unity Editor mixer generator. Paths point to
    // the replacement mixer after the stock dry World branches are reparented
    // below GAL Passive. Only direct AudioSources are moved to these inputs.
    internal readonly struct ContrastRouteSpec
    {
        internal const string InputGroupName = "GAL Contrast Input";

        internal readonly string ParentPath;
        internal readonly string Parameter;

        internal ContrastRouteSpec(string parentPath, string parameter)
        {
            ParentPath = parentPath;
            Parameter = parameter;
        }

        internal string InputPath => ParentPath + "/" + InputGroupName;
        internal string ParentName
        {
            get
            {
                int separator = ParentPath.LastIndexOf('/');
                return separator < 0 ? ParentPath : ParentPath.Substring(separator + 1);
            }
        }
    }

    internal static class GunshotContrastMixerRouteTable
    {
        internal static readonly ContrastRouteSpec[] Routes =
        {
            new ContrastRouteSpec("World/GAL Passive/Main/Environment", "GAL_ContrastRoute00"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ClientPlayer", "GAL_ContrastRoute01"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ClientPlayer/ClientPlayerMovement", "GAL_ContrastRoute02"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ClientPlayer/ClientPlayerSpeech", "GAL_ContrastRoute03"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ClientPlayer/ClientPlayerSelfSpeechReverb", "GAL_ContrastRoute04"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ObservedPlayer", "GAL_ContrastRoute05"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ObservedPlayer/ObservedPlayerMovement", "GAL_ContrastRoute06"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/ObservedPlayer/ObservedPlayerSpeech", "GAL_ContrastRoute07"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/NPC", "GAL_ContrastRoute08"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/NPC/VehicleInSpeech", "GAL_ContrastRoute09"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/TechnicalSounds", "GAL_ContrastRoute10"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/NatureSounds", "GAL_ContrastRoute11"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/CommonSounds", "GAL_ContrastRoute12"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/TechnicalSounds/Vehicles", "GAL_ContrastRoute13"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/TechnicalSounds/Vehicles/VehicleIn", "GAL_ContrastRoute14"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/TechnicalSounds/Vehicles/VehicleOut", "GAL_ContrastRoute15"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/TechnicalSounds/Hideout", "GAL_ContrastRoute16"),
            new ContrastRouteSpec("Inventory", "GAL_ContrastRoute17"),
            new ContrastRouteSpec("World/GAL Passive/Guns/Instrumental", "GAL_ContrastRoute18"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient", "GAL_ContrastRoute19"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbCommonEffects", "GAL_ContrastRoute20"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn", "GAL_ContrastRoute21"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut", "GAL_ContrastRoute22"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/Rain", "GAL_ContrastRoute23"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/Radio", "GAL_ContrastRoute24"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/RadioLessVerb", "GAL_ContrastRoute25"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/OccludedIn", "GAL_ContrastRoute26"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/AmbientInDayGroupBypass", "GAL_ContrastRoute27"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/AmbientInNightGroupBypass", "GAL_ContrastRoute28"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/CommonAmbInEffects", "GAL_ContrastRoute29"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/CommonAmbInBypass", "GAL_ContrastRoute30"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/PrecipitationAmbientIn", "GAL_ContrastRoute31"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/CommonAmbOutEffects", "GAL_ContrastRoute32"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/CommonAmbOutBypass", "GAL_ContrastRoute33"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/PrecipitationAmbientOut", "GAL_ContrastRoute34"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/OutEnvironment", "GAL_ContrastRoute35"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/AmbientOutDay", "GAL_ContrastRoute36"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/AmbientOutNight", "GAL_ContrastRoute37"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/AmbientOutDay/AmbientOutDayEffects", "GAL_ContrastRoute38"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/AmbientOutDay/AmbientOutDayBypass", "GAL_ContrastRoute39"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/AmbientOutNight/AmbientOutNightEffects", "GAL_ContrastRoute40"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/AmbientOutNight/AmbientOutNightBypass", "GAL_ContrastRoute41"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/RadioIn", "GAL_ContrastRoute42"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientIn/RadioInLessVerb", "GAL_ContrastRoute43"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/RadioOut", "GAL_ContrastRoute44"),
            new ContrastRouteSpec("World/GAL Passive/Main/Ambient/AmbientOut/RadioOut/EventRadio", "GAL_ContrastRoute45"),
            new ContrastRouteSpec("World/GAL Passive/Main/Environment/TechnicalSounds/Vehicles/VehicleIn/VehicleInRadio", "GAL_ContrastRoute46"),
            new ContrastRouteSpec("World/GAL Passive/Occlusion", "GAL_ContrastRoute47"),
            new ContrastRouteSpec("World/GAL Passive/Occlusion/Occlusion", "GAL_ContrastRoute48"),
            new ContrastRouteSpec("World/GAL Passive/Occlusion/LowerOccluded", "GAL_ContrastRoute49"),
            new ContrastRouteSpec("World/GAL Passive/Occlusion/UpperOccluded", "GAL_ContrastRoute50"),
            new ContrastRouteSpec("World/GAL Passive/Occlusion/SimpleOccluded", "GAL_ContrastRoute51")
        };
    }
}

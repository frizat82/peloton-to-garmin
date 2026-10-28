using Dynastream.Fit;
using System.Collections.Generic;

namespace Conversion;

public static class ExerciseMapping
{
	public static IReadOnlyCollection<string> IgnoredPelotonExercises = new HashSet<string>()
	{
		// 3
		/* 360 Breath */ "a0206ce0cff341f6a7e967c8f3fa412d",

		// A
		/* Activation */ "d291fe12a06d4edd9ebd73c5b1c9a3ab",
		/* Active Recovery */ "b067c9f7a1e4412190b0f8eb3c6128e3",
		/* AMRAP */ "98cde50f696746ff98727d5362229cfb",

		// D
		/* Demo */ "e54412161b594e54a86d6ef23ea3d017",

		// F
		/* Finisher */ "cafef2f104b54364bffd11e146751b23",

		// H
		/* Hold Breath */ "1293353c065e4f03a5d489db3e1a020a",

		// T
		/* Transition */ "a76c5edc0641475189442ecad456057a",

		// W
		/* Warmup */ "0b67e950426440fe8b4eadc426320a56",
	};

	public static readonly Dictionary<string, GarminExercise> StrengthExerciseMappings = new()
	{
		// 9
		/* 90 90 Stretch */ { "c06ae13cc4f44bae908897e7baa116f5", new (ExerciseCategory.WarmUp, WarmUpExerciseName.Stretch90_90) },

		// A
		/* Arnold Press */ { "01251235527748368069f9dc898aadf3", new (ExerciseCategory.ShoulderPress, ShoulderPressExerciseName.ArnoldPress) },
		/* Arm Circles */ { "1b3400e0aade45e58d2f42cf19af0f40", new (ExerciseCategory.WarmUp, WarmUpExerciseName.ArmCircles) },
		/* Articulating Bridge */ { "295080800830453da54cf35357ad16a4", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.HipRaise) },

		// B
		/* Bear Crawl */ { "46112d47abe24b53ad1fa8e75edcf545", new (ExerciseCategory.Plank, PlankExerciseName.BearCrawl) },
		/* Bear Plank */ { "2311bfd595104970a314fa42858a5811", new (ExerciseCategory.Plank, PlankExerciseName.BearCrawl) },
		/* Bench Step Up */ { "44078488b33b47d686a1aea3038c94ce", new (ExerciseCategory.Squat, SquatExerciseName.StepUp) },
		/* Bent Over Row */ { "d60a1dd8824a49a4926f826b24f3b061", new (ExerciseCategory.Row, RowExerciseName.OneArmBentOverRow) },
		/* Bicep Curl */ { "43d404595338443baab306a6589ae7fc", new (ExerciseCategory.Curl, CurlExerciseName.StandingDumbbellBicepsCurl) },
		/* Bicycle */ { "550b2c852a9547b18ca8e6240c5c6750", new (ExerciseCategory.Crunch, CrunchExerciseName.BicycleCrunch) },
		/* Bird Dog */ { "df8e18c5082f408b8490c4adcb0678b5", new (ExerciseCategory.Plank, PlankExerciseName.PlankWithKneeToElbow) },
		/* Boat Pose */ { "adbac11d3b714403ba22a175a8c95837", new (ExerciseCategory.Crunch, CrunchExerciseName.HollowRock) },
		/* Body Weight Squat */ { "46d30aa1a4a245ada8a5e5ff8f3e7662", new (ExerciseCategory.Squat, SquatExerciseName.Squat) },
		/* Bridge & Reach */ { "0bec94a04d494c7f839a340ee5b323bf", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.HipRaise) },
		/* Bridge Pose */ { "3b2cb5aa464343548f699d0f328efa03", new (ExerciseCategory.Pose, PoseExerciseName.Bridge) },
		/* Bridge Series */ { "51b07b61554a463dbefff2bbe9447945", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.HipRaise) },
		/* Bridge Walkout */ { "d55d9db0c9dc447bb146527391b54e53", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.HipRaise) },
		/* Bridging Clamshell */ { "54fb04d9748b4be0a25a47b9fba168e9", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.ClamBridge) },
		/* Burpee */ { "879f28669dd14f80b2f4f29e3e316d61", new (ExerciseCategory.TotalBody, TotalBodyExerciseName.Burpee) },
		/* Butterfly Sit Up */ { "afa31061deb34341bbb44e6550fc310c", new (ExerciseCategory.SitUp, SitUpExerciseName.ButterflySitUp) },

		// C
		/* Calf Raise */ { "08f8920aed3745cfa367b4c91e8f8e05", new (ExerciseCategory.CalfRaise, CalfRaiseExerciseName.StandingCalfRaise) },
		/* Calf Raise Squat */ { "46158da351134577bf79900cc6239d1f", new (ExerciseCategory.Squat, SquatExerciseName.Squat) },
		/* Cat Cow */ { "5734a4e7475f462687053203f88d55ad", new (ExerciseCategory.Core, CoreExerciseName.CatCow) },
		/* Chest Fly */ { "45207949aa384783b5d71451f7fe1c3d", new (ExerciseCategory.Flye, FlyeExerciseName.DumbbellFlye) },
		/* Chest Opener */ { "8213e50b2cc340dbaca3eb6ad832f49e", new (ExerciseCategory.WarmUp, WarmUpExerciseName.ChestOpeners) },
		/* Clam Shell */ { "5749daaf9be3448397af9d813d760ff3", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.Clams) },
		/* Clean */ { "f2a28d3ebf3c4844a704d2b94e283099", new (ExerciseCategory.OlympicLift, OlympicLiftExerciseName.DumbbellClean) },
		/* Cobra Pose */ { "81b78f8813284c1eac1190cc5bad24b4", new (ExerciseCategory.Hyperextension, HyperextensionExerciseName.Cobra) },
		/* Concentrated Curl */ { "3695ef0ec2ce484faedc8ce2bfa2819d", new (ExerciseCategory.Curl, CurlExerciseName.SeatedDumbbellBicepsCurl) },
		/* Copenhagen Plank */ { "7f71e09975e345ebaf71d8db2c726a0f", new (ExerciseCategory.Plank, PlankExerciseName.SidePlank) },
		/* Crab Reach */ { "cf16f941fc4b4baaa2f72f13cd6b8359", new (ExerciseCategory.Plank, PlankExerciseName.LyingReversePlank) },
		/* Criss-Cross */ { "b62c0d2189ae4c2fb6f5203fa145010a", new (ExerciseCategory.Core, CoreExerciseName.CrissCross) },
		/* Cross Body Bicep Curl */ { "d1129566ef1a4d878019eaef36712734", new (ExerciseCategory.Curl, CurlExerciseName.CrossBodyDumbbellHammerCurl) },
		/* Crunch */ { "61ac0d64602c48fba25af7e5e5dc1f97", new (ExerciseCategory.Crunch, CrunchExerciseName.Crunch) },
		/* Cursh Press */ { "4899db2664ce47da8ec14282282d3b0d", new (ExerciseCategory.BenchPress, BenchPressExerciseName.CloseGripBarbellBenchPress) },
		/* Cross Body Curl */ { "a66b797fc2014b799cc0cb114d9c5079", new (ExerciseCategory.Curl, CurlExerciseName.CrossBodyDumbbellHammerCurl) },
		/* Curtsy Lunge */ { "353c3a137bf44ebeb3e94d4fb0a4f0a1", new (ExerciseCategory.Lunge, LungeExerciseName.ReverseDumbbellCrossoverLunge) },

		// D
		/* Dead Bug */ { "3001f790c7ca471e8ba6d1b57a3a842d", new (ExerciseCategory.HipStability, HipStabilityExerciseName.DeadBug) },
		/* Deadlift */ { "cd6046306b2c4c4a8f40e169ec924eb9", new (ExerciseCategory.Deadlift, DeadliftExerciseName.DumbbellDeadlift) },
		/* Devil's Press */ { "41c0d7385ac54add8d22f035b92e588a", new (ExerciseCategory.TotalBody, TotalBodyExerciseName.WeightedBurpee) },
		/* Diagonal Chop */ { "eef5db32dd88452090257cf52e22f34e", new (ExerciseCategory.Chop, ChopExerciseName.DumbbellChop) },
		/* Double Leg Stretch */ { "c25b2d80207541eca30ddd2c3a4e49ea", new (ExerciseCategory.Core, CoreExerciseName.DoubleLegStretch) },
		/* Double Straight Leg Stretch */ { "cd97793973cd4a7cb722d9c8e97b6551", new (ExerciseCategory.Core, CoreExerciseName.DoubleLegStretch) },
		/* Downward Facing Dog Pose */ { "a8f5f9d4189b4692aa6f5e15f6e040b6", new (ExerciseCategory.Pose, PoseExerciseName.DownwardFacingDog) },
		/* Drop Squat */ { "0e51c00d86994e6e8626d27a0716f408", new (ExerciseCategory.Squat, SquatExerciseName.Squat) },
		/* Dumbbell Lat Pull Over */ { "25f1731b59e040989452ff1598991340", new (ExerciseCategory.PullUp, PullUpExerciseName.EzBarPullover) },
		/* Dumbbell Pushup */ { "ce8c746fb5224e9dbc401fef0013a54f", new (ExerciseCategory.PushUp, PushUpExerciseName.PushUp) },
		/* Dumbbell Single Leg Deadlift */ { "6dd608bcc9394b49a68a918359839202", new (ExerciseCategory.Deadlift, DeadliftExerciseName.SingleLegRomanianDeadliftWithDumbbell) },
		/* Dumbbell Squat */ { "7d82b59462a54e61926077ded0becae5", new (ExerciseCategory.Squat, SquatExerciseName.DumbbellSquat) },
		/* Dumbbell Standing Good Morning */ { "641e78908f5b4e1682df02646f15aaeb", new (ExerciseCategory.LegCurl, LegCurlExerciseName.GoodMorning) },
		/* Dumbbell Sumo Deadlisft */ { "cd25b61809884d60adb1d97cd646f4fd", new (ExerciseCategory.Deadlift, DeadliftExerciseName.SumoDeadlift) },
		/* Dumbbell Sumo Squat */ { "0f2c2850b7ad4173b3cd4f1abe15abd4", new (ExerciseCategory.Squat, SquatExerciseName.SumoSquat) },
		/* Dumbbell Swing */ { "4460e019d86c4e4ebe7284bb16d128d2", new (ExerciseCategory.HipSwing, HipSwingExerciseName.SingleArmDumbbellSwing) },
		/* Dumbbell Thruster */ { "5ab0baeebee94d3995cb7f2b0332f430", new (ExerciseCategory.Squat, SquatExerciseName.Thrusters) },
		/* Dolphin */ { "843d434f59f941c0826fc0fe15eb0236", new (ExerciseCategory.Plank, PlankExerciseName.PlankPikes) },

		// F
		/* Farmer Carry */ { "88a2dd78ed254240bce4f2b7f1a2c75e", new (ExerciseCategory.Carry, CarryExerciseName.FarmersCarry) },
		/* Flutter Kick */ { "6091566fa0674afd96a22fcec3ab18ce", new (ExerciseCategory.Crunch, CrunchExerciseName.FlutterKicks) },
		/* Forearm Side Plank */ { "1c0403c4d7264d83b1c75d18c8cdac4f", new (ExerciseCategory.Plank, PlankExerciseName.SidePlank) },
		/* Forearm Side Plank Hip Dip */ { "a395acf0dd27412888494c990c4774f6", new (ExerciseCategory.Plank, PlankExerciseName.SidePlankLift) },
		/* Forearm Side Plank Rotation */ { "223e8e6918d64d9097064d34e3b17e77", new (ExerciseCategory.Plank, PlankExerciseName.SidePlankWithReachUnder) },
		/* ForeArm Plank */ { "feb44f24e2b8487b870a35f4501069be", new (ExerciseCategory.Plank, PlankExerciseName.Plank) },
		/* French Door */ { "78b0a09ce8274e9c8beac6aadd50454b", new (ExerciseCategory.TricepsExtension, TricepsExtensionExerciseName.DumbbellLyingTricepsExtension) },
		/* Front to Back Lunge */ { "ed18d837c14746c5af38d4fa03b56918", new (ExerciseCategory.Lunge, LungeExerciseName.DumbbellLunge) },
		/* Front Lunge */ { "8ef53816dc414bed800e8bf0cee3c484", new (ExerciseCategory.Lunge, LungeExerciseName.DumbbellLunge) },
		/* Front Raise */ { "a9cefac3b8234bc0bc0ee8deb62d67d3", new (ExerciseCategory.LateralRaise, LateralRaiseExerciseName.FrontRaise) },
		/* Front Raise Circles */ { "e099e49b59564ef4a7198bf87cdc1446", new (ExerciseCategory.LateralRaise, LateralRaiseExerciseName.FrontRaise) },

		// G
		/* Goblet Squat */ { "588e35f7067842979485ff1e4f80df26", new (ExerciseCategory.Squat,SquatExerciseName.GobletSquat) },
		/* Gorilla Row */ { "0207f43c05c4438e9b0b7770854fd505", new (ExerciseCategory.Row, RowExerciseName.OneArmBentOverRow) },

		// H
		/* Half Turkish Get-up */ { "2703c1f3bbbd486eba75b75c463b8ebb", new (ExerciseCategory.Core, CoreExerciseName.HalfTurkishGetUp) },
		/* Hammer Curl */ { "114ce849b47a4fabbaad961188bf4f7d", new (ExerciseCategory.Curl, CurlExerciseName.DumbbellHammerCurl) },
		/* Hand Release Push Up */ { "6dd46aa45e9042e19ec5a59d2ff02bec", new (ExerciseCategory.PushUp, PushUpExerciseName.HandReleasePushUp) },
		/* High Plank */ { "194cc4f6a88c4abd80afe9bbddb25915", new (ExerciseCategory.Plank, PlankExerciseName.StraightArmPlank) },
		/* High Pull */ { "d06c8e68481741de849e4101eda76855", new (ExerciseCategory.Shrug, ShrugExerciseName.DumbbellUprightRow) },
		/* High Side Plank */ { "a6833a0f9c35489585398d1f293600de", new (ExerciseCategory.Plank, PlankExerciseName.SidePlank) },
		/* High Side Plank Hip Dip */ { "7b7e4f393cdd4244916fb2dec1d9c4b6", new (ExerciseCategory.Plank, PlankExerciseName.SidePlankLift) },
		/* Hip Bridge */ { "06a504988ace45faabd927af1479f454", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.HipRaise) },
		/* Hip Circle */ { "01b6235c824c43f4a8e42d6cfc88a18d", new (ExerciseCategory.HipStability, HipStabilityExerciseName.HipCircles) },
		/* Hollow Hold */ { "060174b84e3744e6a19fe4ce80411113", new (ExerciseCategory.Crunch, CrunchExerciseName.HollowRock) },
		/* Hollow Rock */ { "b264d06330c5442d83ffeaff878cf31d", new (ExerciseCategory.Crunch, CrunchExerciseName.HollowRock) },
		/* Human-maker */ { "28f123279cfd47f6838b4c1322bc8473", new (ExerciseCategory.TotalBody, TotalBodyExerciseName.ManMakers) },
		/* Hundreds */ { "e796ebf734614aec9d028321542e78f4", new (ExerciseCategory.Core, CoreExerciseName.TheHundred) },

		// I
		/* In And Out Squat */ { "6b00c9468eb049b4ac827d82a800e4ab", new (ExerciseCategory.Squat, SquatExerciseName.SquatJumpsInNOut) },
		/* Inchworm */ { "3729a62f44564cf7a773ffe4cf60c86d", new (ExerciseCategory.Core, CoreExerciseName.Inchworm) },
		/* Internal-External Rotation */ { "3c86994628034649b621a3f969e66050", new (ExerciseCategory.ShoulderStability, ShoulderStabilityExerciseName.LyingExternalRotation) },

		// J
		/* Jerk */ { "724d071f7f2e4c29a27cecd6e454e531", new (ExerciseCategory.OlympicLift, OlympicLiftExerciseName.PushJerk) },
		/* Jumping Jack */ { "fe07adf7c7ba4da688b996194119a90b", new (ExerciseCategory.Cardio, CardioExerciseName.JumpingJacks) },

		// K
		/* Kickstand Deadlift */ { "9c555e18992345278e37cbb51fa2867d", new (ExerciseCategory.Deadlift, DeadliftExerciseName.SingleLegRomanianDeadliftWithDumbbell) },
		/* Knee Fold Toe Tap */ { "dcd72f92660d4469b2e673408eb700ae", new (ExerciseCategory.Core, CoreExerciseName.KneeFolds) },
		/* Kneeling Side Kick Pilates */ { "49ce96b18d504f09819e3689f5b76196", new (ExerciseCategory.Plank, PlankExerciseName.SideKickPlank) },
		/* Knees To Elbows */ { "8d2c046a3c884564a5cda9f273016768", new (ExerciseCategory.Crunch, CrunchExerciseName.KneesToElbow) },

		// L
		/* Lateral Lunge */ { "fb63e1ea19264145ae6856eefacbcb22", new (ExerciseCategory.Lunge, LungeExerciseName.SlidingLateralLunge)},
		/* Lateral Bear Crawl */ { "f0fea2eeb5f247be85859207af1e48ea", new (ExerciseCategory.Plank, PlankExerciseName.BearCrawl) },
		/* Lateral Squat */ { "db1b398cc76c4093b1124a4e2d2748a9", new (ExerciseCategory.Lunge, LungeExerciseName.SideLunge) },
		/* Lawn Mower Row */ { "c80ac3adb6f74487808f361876ba326c", new (ExerciseCategory.Row, RowExerciseName.OneArmBentOverRow)},
		/* Lateral Raise */ { "2635cbe093a140e0be83be83fa594d8b", new (ExerciseCategory.LateralRaise, LateralRaiseExerciseName.SeatedLateralRaise)},
		/* Lateral Leg Lift */ { "1b087d2dd8964130b803754354c47c90", new (ExerciseCategory.HipStability, HipStabilityExerciseName.SideLyingLegRaise) },
		/* Leg Circles */ { "fc0415faa765462e985abd17ffd2d5fe", new (ExerciseCategory.Core, CoreExerciseName.SingleLegCircles) },
		/* Leg Lift */ { "7a5f7d80783f4b77b44dad8d6a0d2fae", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.LegLift)},
		/* Leg Pull Front */ { "25e2db583cf14e2c9056b9d96abfd914", new (ExerciseCategory.Plank, PlankExerciseName.PlankWithLegLift) },
		/* Leg Raise */ { "281e8c9d6d874bb9a1fc1fb9deac0c9b", new (ExerciseCategory.LegRaise, LegRaiseExerciseName.LyingStraightLegRaise) },
		/* Lunge Jump */ { "5e848323f12343c3a12f36358d56234b", new (ExerciseCategory.Plyo, PlyoExerciseName.AlternatingJumpLunge) },

		// M
		/* March */ { "34634dbd7b064923b533e8dcd6a86c20", new (ExerciseCategory.WarmUp, WarmUpExerciseName.MarchingInPlace) },
		/* Marching Farmer Carry */ { "3f672cb7180348878dc56d4b3c354268", new (ExerciseCategory.Carry, CarryExerciseName.FarmersWalk) },
		/* Marching Overhead Carry */ { "4b396dcfda9148b0b0c430c495e6703b", new (ExerciseCategory.Carry, CarryExerciseName.OverheadCarry) },
		/* Mermaid Stretch */ { "f4f166f4a8134a9abd91aba524ba88ca", new (ExerciseCategory.Move, MoveExerciseName.MermaidStretch) },
		/* Mountain Climber */ { "b16ea86247b34c9e814b074a757524f5", new (ExerciseCategory.Plank, PlankExerciseName.MountainClimber) },

		// N
		/* Narrow Squat */ { "63b0566dbaff495187421b3b55be605c", new (ExerciseCategory.Squat, SquatExerciseName.Squat) },
		/* Narrow-Grip Overhead Press */ { "3caccc04fea1402cab9887ce589833ea", new (ExerciseCategory.ShoulderPress, ShoulderPressExerciseName.OverheadDumbbellPress) },
		/* Neutral Grip Chest Press */ { "802f10996b5048d08f320d8661f13ee1", new (ExerciseCategory.BenchPress, BenchPressExerciseName.NeutralGripDumbbellBenchPress) },

		// O
		/* Oblique Crunch */ { "f52100cb5df44c7795ac9d7a09e4d0b9", new (ExerciseCategory.Crunch, CrunchExerciseName.BicycleCrunch) },
		/* Oblique Heel Tap */ { "d5ec25fe793f4318a6891607bd3c9259", new (ExerciseCategory.Crunch, CoreExerciseName.SideBend) },
		/* Offset Push Up */ { "b5d8c5e2e78c49d497f67b03e39e9582", new (ExerciseCategory.PushUp, PushUpExerciseName.PushUp) },
		/* Offset Squat */ { "e95ba17532ec483b802f670477a0e1da", new (ExerciseCategory.Squat, SquatExerciseName.DumbbellSquat) },
		/* Open Lateral Raise */ { "00046ee377554425866b0a1963b98589", new (ExerciseCategory.LateralRaise, LateralRaiseExerciseName.SeatedLateralRaise) },
		/* Overhead Carry */ { "12057d5f9e144913a824bcae5706966c", new (ExerciseCategory.Carry, CarryExerciseName.OverheadCarry) },
		/* Overhead Extension */ { "f260623343e74d37b165071ee5903199", new (ExerciseCategory.TricepsExtension, TricepsExtensionExerciseName.OverheadDumbbellTricepsExtension) },
		/* Overhead Press */ { "ef0279948228409298cd6bf62c5b122c", new (ExerciseCategory.ShoulderPress, ShoulderPressExerciseName.OverheadDumbbellPress) },

		// P
		/* Pelvic Tilt */ { "df8efaae0e294f7a94be2f9267222bfb", new (ExerciseCategory.Core, CoreExerciseName.PelvicClocks) },
		/* Pike Push Up */ { "8af39d3485224ac19f7d8659d30524e7", new (ExerciseCategory.PushUp, PushUpExerciseName.ShoulderPushUp) },
		/* Pilates Clamshell */ { "39233245302f4901bd4be899fa51080a", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.Clams) },
		/* Pilates Push Up */ { "748d6bbfe8aa4ff28fb30dca13d6e7c5", new (ExerciseCategory.PushUp, PushUpExerciseName.PilatesPushup) },
		/* Plank March */ { "8e073491fbe5458ebfcc7c4d70aa22ba", new (ExerciseCategory.Plank, PlankExerciseName.Plank) },
		/* Prone Back Strengthener */ { "88d3543dad1c43be8dc9a2321a662b19", new (ExerciseCategory.Hyperextension, HyperextensionExerciseName.SupermanFromFloor) },
		/* Prowler Push Up */ { "d81cd1016c374c6ca295dc75bf003ff7", new (ExerciseCategory.PushUp, PushUpExerciseName.PushUp) },
		/* Pulsing Split Squat */ { "5b862af7d2924ac5ab46494318415af7", new (ExerciseCategory.Squat, SquatExerciseName.DumbbellSplitSquat) },
		/* Push Up */ { "1c4d81ad487849a6995f93e1a6a4b1e4", new (ExerciseCategory.PushUp, PushUpExerciseName.PushUp) },
		/* Plank Hand Tap */ { "ad8e4f16bf5a450db7d3b72b8ff7b014", new (ExerciseCategory.Plank, PlankExerciseName.StraightArmPlankWithShoulderTouch) },
		/* Plank Pike Reach */ { "67c956e4da6542d1bbfa0625d569f018", new (ExerciseCategory.Plank, PlankExerciseName.PlankPikes) },
		/* Push Press */ { "ae8ada57d3f0424ba391effec04e1e5f", new (ExerciseCategory.ShoulderPress, ShoulderPressExerciseName.DumbbellPushPress) },

		// R
		/* Reverse Crunch */ { "ceb58dd70731482dae8d365e6ea3e708", new (ExerciseCategory.Crunch, CrunchExerciseName.ReverseCrunch) },
		/* Reverse Lunge */ { "c430accc3802486a86ad2de9cb8f01cc", new (ExerciseCategory.Lunge, LungeExerciseName.ReverseSlidingLunge) },
		/* Reverse Fly */ { "3df6a1136a7a4e4db31e104c7d5a0fcf", new (ExerciseCategory.Flye, FlyeExerciseName.InclineDumbbellFlye) },
		/* Renegade Row */ { "ed9adea36e77459dab7c189884ceb7ab", new (ExerciseCategory.Row, RowExerciseName.RenegadeRow) },
		/* Reverse Plank */ { "6e51ce9b6fb4450eb3345fe8243c88ac", new (ExerciseCategory.Plank, PlankExerciseName.LyingReversePlank) },
		/* Roll Up */ { "0b853e45afb04c31968b20fc7deaa718", new (ExerciseCategory.Core, CoreExerciseName.RollUp) },
		/* Rolling Like A Ball */ { "4a029f7f66a147e2b5ff56bfe4132218", new (ExerciseCategory.Core, CoreExerciseName.Rolling) },
		/* Row */ { "165ed4b439204800b9d88d85363f0609", new (ExerciseCategory.Row, RowExerciseName.DumbbellRow) },
		/* Romanian Deadlift */ { "a17b8d35d1264a2fbabe3ab28df458dc", new (ExerciseCategory.Deadlift, DeadliftExerciseName.DumbbellDeadlift) },// RDL exists in Connect but not in SDK definition
		/* Runners Lunge */ { "3ca0b431086245a4b896b5a3f97f316f", new (ExerciseCategory.Lunge, LungeExerciseName.RunnersLungeToBalance) },
		/* Russian Twist */ { "5c7b2bc65abc4c44849e2119f1338120", new (ExerciseCategory.Core, CoreExerciseName.RussianTwist) },

		// S
		/* Saw */ { "0f11c916790b4f27b86151cd3776cde5", new (ExerciseCategory.Move, MoveExerciseName.Saw) },
		/* Scissor Kick */ { "f6a10df381004afba2a2b63447d9968f", new (ExerciseCategory.Crunch, CrunchExerciseName.LegLevers) },
		/* Scissors */ { "0f9b2d6f18b247bd950d60bbbefd19f3", new (ExerciseCategory.Crunch, CrunchExerciseName.LegLevers) },
		/* Seated Crunch */ { "440c5623e95c4b7ca3dc57ff61be349a", new (ExerciseCategory.Crunch, CrunchExerciseName.Crunch) },
		/* Shoulder Bridge */ { "fc8131d729214257bfda5d13e6f92b6e", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.HipRaise) },
		/* Shoulder Bridge Leg Lifts */ { "6f79d9a4faba4620838b5ada371a5fd1", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.BridgeWithLegExtension) },
		/* Shoulder Tap */ { "5b33283433e7479390c0d5fc11722f80", new (ExerciseCategory.Plank, PlankExerciseName.StraightArmPlankWithShoulderTouch) },
		/* Side Kick Pilates */ { "1651a73de6354111a86c6cd00b206889", new (ExerciseCategory.HipStability, HipStabilityExerciseName.SideLyingLegRaise) },
		/* Side Lying Leg Lift */ { "0fd6f14d2ce64a0082b5c3e19c9a0203", new (ExerciseCategory.HipStability, HipStabilityExerciseName.SideLyingLegRaise) },
		/* Side Lying Series */ { "45aa90411510461a977b887af63cf9c0", new (ExerciseCategory.HipStability, HipStabilityExerciseName.SideLyingLegRaise) },
		/* Single Arm Jerk */ { "086a82e422674dba884fbd43e6c7a9ad", new (ExerciseCategory.OlympicLift, OlympicLiftExerciseName.PushJerk) },
		/* Single Arm Press */ { "97f0a46ff7ad4f03ac9b2cfac96e3b40", new (ExerciseCategory.ShoulderPress, ShoulderPressExerciseName.OverheadDumbbellPress) },
		/* Single Arm Bicep Curl */ { "a466cebb07794281a367dd686794aa62", new (ExerciseCategory.Curl, CurlExerciseName.StandingDumbbellBicepsCurl) },
		/* Single Leg Deadlift to Iso Hold */ { "aaff0cbe0236496f9d7c640a253ad411", new (ExerciseCategory.Deadlift, DeadliftExerciseName.SingleLegRomanianDeadliftWithDumbbell) },
		/* Single Leg Hip Bridge */ { "9895235fe20244c8b347b1cd9db82d2d", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.SingleLegHipRaise) },
		/* Single Leg Shoulder Bridge */ { "0bed7a2c39e3425991bf376e1c704c07", new (ExerciseCategory.HipRaise, HipRaiseExerciseName.SingleLegHipRaise) },
		/* Single Leg Stretch */ { "32c3f3f1f90446ad8e58589b45ae891b", new (ExerciseCategory.Core, CoreExerciseName.SingleLegStretch) },
		/* Single Leg V-Up */ { "84b9b4b6c48545fdaeaa8e26e57b3459", new (ExerciseCategory.SitUp, SitUpExerciseName.VUp) },
		/* Sit Up */ { "b6a2867d726442fea5f87f82dfdf7a5d", new (ExerciseCategory.SitUp, SitUpExerciseName.SitUp) },
		/* Skater Hop */ { "9f70116dae1d4496be1e8c556d6f9f38", new (ExerciseCategory.Plyo, PlyoExerciseName.LateralLeapAndHop) },
		/* Skull Crusher */ { "3c72e60de73d43f4b5a774c90dea90cd", new (ExerciseCategory.TricepsExtension, TricepsExtensionExerciseName.DumbbellLyingTricepsExtension) },
		/* Snatch */ { "0ddf8f94acfe4c2289aef5a9bf59e8d9", new (ExerciseCategory.OlympicLift, OlympicLiftExerciseName.SingleArmDumbbellSnatch) },
		/* Spine Twist */ { "84b92c2d05ad4fc0a1a6e575ea6d44da", new (ExerciseCategory.Move, MoveExerciseName.SpinalTwist) },
		/* Split Squat */ { "28833fd99466476ea273d6b94747e3db", new (ExerciseCategory.Squat, SquatExerciseName.DumbbellSplitSquat) },
		/* Squat Jack */ { "05daa15f53744975bc332d80e3987a53", new (ExerciseCategory.Cardio, CardioExerciseName.SquatJacks) },
		/* Squat Thrust */ { "594625a48d7848e199106111e648bcce", new (ExerciseCategory.TotalBody, TotalBodyExerciseName.SquatThrusts) },
		/* Standing Chest Fly */ { "021047bf0cff470bb2d11f94d3539cfe", new (ExerciseCategory.Flye, FlyeExerciseName.DumbbellFlye) },
		/* Standing Good Morning */ { "3bda651aed1f43059a0ec95a27edd0e5", new (ExerciseCategory.LegCurl, LegCurlExerciseName.GoodMorning) },
		/* Straight Leg Bicycle */ { "b617914877c24dac85df81621872e056", new (ExerciseCategory.Core, CoreExerciseName.Bicycle) },
		/* Superhuman */ { "88bd5bda699a46a3977381bfc2e968e3", new (ExerciseCategory.Hyperextension, HyperextensionExerciseName.SupermanFromFloor) },
		/* Supinated Row */ { "34dd5f694fd44d15bb0eead604dfebae", new (ExerciseCategory.Row, RowExerciseName.ReverseGripBarbellRow) },
		/* Swan Dive */ { "5cdc102ef0644da5a64a46df3110702b", new (ExerciseCategory.Core, CoreExerciseName.Swan) },
		/* Swan Prep */ { "5697fb9e1ff74e0ea372d62e8b32d62f", new (ExerciseCategory.Core, CoreExerciseName.Swan) },
		/* Swimming */ { "3b45dfc47bab4880ab4a8deaacb2b5a8", new (ExerciseCategory.Core, CoreExerciseName.Swimming) },
		/* Swimmers */ { "c55dc9b71ec541d29fdee3021c77373a", new (ExerciseCategory.Core, CoreExerciseName.Swimming) },

		// T
		/* T Spine Rotation */ { "cc87938b004f49849f515991010967df", new (ExerciseCategory.WarmUp, WarmUpExerciseName.ThoracicRotation) },
		/* Table Top Lateral Leg Lift */ { "5bb2d37f052e4d2faf1b0f1de4489531", new (ExerciseCategory.Plank, PlankExerciseName.KneelingSidePlankWithLegLift) },
		/* Tall Kneeling Side Bend */ { "6be91da1de1c49f4b34bf358bdbf3bbc", new (ExerciseCategory.Crunch, CrunchExerciseName.StandingSideCrunch) },
		/* Tate Press */ { "01f1e64df5c543e489801b1cf1808f33", new (ExerciseCategory.TricepsExtension, TricepsExtensionExerciseName.DumbbellLyingTricepsExtension) },
		/* Teaser */ { "7716941202de4e89b369a62df8f93847", new (ExerciseCategory.Core, CoreExerciseName.Teaser) },
		/* Tick Tock */ { "ec8415d086fc4646a51878f47dd03e8a", new (ExerciseCategory.Move, MoveExerciseName.TickTockSideRoll) },
		/* Traveling Squat */ { "bf640f6353e747e590d405804bc76652", new (ExerciseCategory.Squat, SquatExerciseName.Squat) },
		/* Tricep Dip */ { "0a983b7dfca7400a92761380ff9d351a", new (ExerciseCategory.TricepsExtension, TricepsExtensionExerciseName.BodyWeightDip) },
		/* Tricep Kickback */ { "da89d743904640d58e8b3f667f08783c", new (ExerciseCategory.TricepsExtension, TricepsExtensionExerciseName.DumbbellKickback) },
		/* Tuck up */ { "3069e7ba28b84005b71c16a3781dda8d", new (ExerciseCategory.SitUp, SitUpExerciseName.BentKneeVUp) },
		/* Turkish Get-Up */ { "9932c03f00ba4d80b061812229666838", new (ExerciseCategory.Core, CoreExerciseName.TurkishGetUp) },
		/* Tricep Push Up */ { "d463a4dc0cf640e0a58f3aa058c5b1a0", new (ExerciseCategory.PushUp, PushUpExerciseName.PushUp) },
		/* Twisting Curl */ { "63c9c4d795974109854a188318cf10e5", new (ExerciseCategory.Curl, CurlExerciseName.StandingAlternatingDumbbellCurls) },
		/* Twisting Mountain Climber */ { "cc70d143627c45e5b64e2cb116619899", new (ExerciseCategory.Plank, PlankExerciseName.CrossBodyMountainClimber) },

		// U
		/* Upright Row */ { "5b81a47429fd45e58156b82450bbdbe2", new (ExerciseCategory.Shrug, ShrugExerciseName.DumbbellUprightRow) },

		// V
		/* V-Up */ { "715caba11593427299342c378b444e05", new(ExerciseCategory.SitUp, SitUpExerciseName.VUp) },

		// W
		/* Woodchop */ { "05d60a7d022f41dd8d3a8da07bca6041", new (ExerciseCategory.Chop, ChopExerciseName.DumbbellChop) },
		/* Wide Grip Chest Press */ { "94e3c37e0cb245f78e195b115a400112", new (ExerciseCategory.BenchPress, BenchPressExerciseName.WideGripBarbellBenchPress) },
		/* Wide Grip Bent Over Row */ { "d861cb497fcc4e1cba994b7a949a3bac", new (ExerciseCategory.Row, RowExerciseName.WideGripSeatedCableRow) },
		/* Wide Grip Overhead Press */ { "258884d9586b45b3973228147a6b0c48", new (ExerciseCategory.ShoulderPress, ShoulderPressExerciseName.OverheadDumbbellPress) },
		/* Wide Row */ { "f05ccd12e95c49aa93ac66bff7ec8df0", new (ExerciseCategory.Row, RowExerciseName.WideGripSeatedCableRow) },
		/* World's Greatest Stretch */ { "2b543e8d932b431a88d296d5677c7ce5", new (ExerciseCategory.WarmUp, WarmUpExerciseName.ElbowToFootLunge) },
		/* Wrist Curl */ { "d5dfeae09db149fca2c5781d0478e87b", new (ExerciseCategory.Curl, CurlExerciseName.DumbbellWristCurl) },
		/* W-Y's */ { "52dfd0316bd94aae9e461d8d3a69dff1", new (ExerciseCategory.LateralRaise, LateralRaiseExerciseName.SeatedLateralRaise) },

		// Z
		/* Zottman Curl */ { "96b11092c5064b779b371462e2509e82", new (ExerciseCategory.Curl, CurlExerciseName.StandingAlternatingDumbbellCurls) }, // Zottman exists in Connect but not in SDK definition
	};

	public static bool IsRest(this string pelotonExerciseId)
	{
		return pelotonExerciseId == "3ca251f6d68746ad91aebea5c89694ca";
	}
}

public record GarminExercise
{
	public ushort ExerciseCategory { get; init; }
	public ushort ExerciseName { get; init; }

	public GarminExercise(ushort exerciseCategory, ushort exerciseName)
	{
		ExerciseCategory = exerciseCategory;
		ExerciseName = exerciseName;
	}
}

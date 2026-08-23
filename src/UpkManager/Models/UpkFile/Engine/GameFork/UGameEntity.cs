using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.GameFork
{
    [UnrealClass("MarvelEntity")]
    public class UGameEntity : UActor
    {
        [PropertyField]
        public UArray<FObject> MAttachmentClasses { get; set; } // UGameAttachment

        [PropertyField]
        public UArray<FName> PhysicsFloppyBones { get; set; }

        [PropertyField]
        public UArray<FAnimationSetAlias> AnimationSetAliases { get; set; }

        [PropertyField]
        public UArray<FObject> ThrowPowerWeakComponents { get; set; } // GameFX

        [PropertyField]
        public UArray<FObject> ThrowPowerStrongComponents { get; set; } // GameFX

        [PropertyField]
        public UArray<FObject> ThrowPutdownPowerWeakComponents { get; set; } // GameFX

        [PropertyField]
        public UArray<FObject> ThrowPutdownPowerStrongComponents { get; set; } // GameFX
    }

    [UnrealStruct("AnimationSetAlias")]
    public class FAnimationSetAlias
    {
        [StructField]
        public FName alias { get; set; }

        [StructField("UAnimSet")]
        public FObject AnimSet { get; set; } // UAnimSet
    }

    [UnrealClass("MarvelAttachment")]
    public class UGameAttachment : UActor
    {
        [PropertyField]
        public EVisibilityPoint VisibilityPoint { get; set; }

        public enum EVisibilityPoint
        {
            VISIBLE_ON_SPAWN,               // 0
            VISIBLE_ON_AGGRO,               // 1
            VISIBLE_ON_ENDURANCE_CHANGE,    // 2
            VISIBLE_ON_THROW,               // 3
            VISIBLE_ON_DEMAND,              // 4
            VISIBLE_ON_MAX                  // 5
        };
    }

    [UnrealClass("MarvelAttachmentMeshBase")]
    public class UGameAttachmentMeshBase : UGameAttachment
    {
        [PropertyField]
        public UArray<FName> AttachmentBones { get; set; }

        [PropertyField]
        public UArray<FName> WeaponSlot { get; set; }
    }

    [UnrealClass("MarvelAttachment_Models")]
    public class UGameAttachment_Models : UGameAttachmentMeshBase
    {
        [PropertyField]
        public UArray<FObject> ModelMesh { get; set; } // SkeletalMesh

        [PropertyField]
        public UArray<FObject> ModelMaterialOverride { get; set; } // MaterialInstance

        [PropertyField]
        public UArray<FObject> ModelPhysicsAsset { get; set; } // PhysicsAsset

        [PropertyField]
        public UArray<FName> ModelPhysicsFloppyBones { get; set; }
    }

    [UnrealClass("MarvelAttachmentAnimated")]
    public class UGameAttachmentAnimated : UGameAttachment_Models
    {
        [PropertyField]
        public UArray<FObject> AnimationSet { get; set; } // AnimSet
    }

    public enum NaviContentTags
    {
        None = 0,
        OpaqueWall = 1,
        TransparentWall = 2,
        Blocking = 3,
        NoFly = 4,
        Walkable = 5,
        Obstacle = 6
    }
}

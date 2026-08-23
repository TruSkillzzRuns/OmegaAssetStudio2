using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.GameFork
{
    [UnrealClass("BoxComponent")]
    public class UBoxComponent : UDrawBoxComponent
    {
    }

    [UnrealClass("MarvelFX")]
    public class UGameFX : UActorComponent
    {
    }

    [UnrealClass("FXAnimation")]
    public class UFXAnimation : UGameFX
    {
    }

    [UnrealClass("ConditionFXAnimation")]
    public class UConditionFXAnimation : UFXAnimation
    {
    }

    [UnrealClass("FXMaterialParameter")]
    public class UFXMaterialParameter : UGameFX
    {
    }

    [UnrealClass("FXAttachmentMaterialParameter")]
    public class UFXAttachmentMaterialParameter : UFXMaterialParameter
    {
    }

    [UnrealClass("ConditionFXAttachmentMaterialParameter")]
    public class UConditionFXAttachmentMaterialParameter : UFXAttachmentMaterialParameter
    {
    }

    [UnrealClass("FXCameraShake")]
    public class UFXCameraShake : UGameFX
    {
    }

    [UnrealClass("ConditionFXCameraShake")]
    public class UConditionFXCameraShake : UFXCameraShake
    {
    }

    [UnrealClass("FXDecal")]
    public class UFXDecal : UGameFX
    {
    }

    [UnrealClass("ConditionFXDecal")]
    public class UConditionFXDecal : UFXDecal
    {
    }

    [UnrealClass("FXHide")]
    public class UFXHide : UGameFX
    {
    }

    [UnrealClass("ConditionFXHide")]
    public class UConditionFXHide : UFXHide
    {
    }

    [UnrealClass("ConditionFXMaterialParameter")]
    public class UConditionFXMaterialParameter : UFXMaterialParameter
    {
    }

    [UnrealClass("ConditionFXMaterialSwap")]
    public class UConditionFXMaterialSwap : UGameFX
    {
    }

    [UnrealClass("FXMeshAttachment")]
    public class UFXMeshAttachment : UGameFX
    {
    }

    [UnrealClass("ConditionFXMeshAttachment")]
    public class UConditionFXMeshAttachment : UFXMeshAttachment
    {
    }

    [UnrealClass("FXMeshScale")]
    public class UFXMeshScale : UGameFX
    {
    }

    [UnrealClass("ConditionFXMeshScale")]
    public class UConditionFXMeshScale : UFXMeshScale
    {
    }

    [UnrealClass("FXMeshSwap")]
    public class UFXMeshSwap : UGameFX
    {
    }

    [UnrealClass("ConditionFXMeshSwap")]
    public class UConditionFXMeshSwap : UFXMeshSwap
    {
    }

    [UnrealClass("FXParticle")]
    public class UFXParticle : UGameFX
    {
    }

    [UnrealClass("ConditionFXParticle")]
    public class UConditionFXParticle : UFXParticle
    {
    }

    [UnrealClass("FXPhysicsWeight")]
    public class UFXPhysicsWeight : UGameFX
    {
    }

    [UnrealClass("ConditionFXPhysicsWeight")]
    public class UConditionFXPhysicsWeight : UFXPhysicsWeight
    {
    }

    [UnrealClass("FXPostProcessing")]
    public class UFXPostProcessing : UGameFX
    {
    }

    [UnrealClass("ConditionFXPostProcessing")]
    public class UConditionFXPostProcessing : UFXPostProcessing
    {
    }

    [UnrealClass("FXSound")]
    public class UFXSound : UGameFX
    {
    }

    [UnrealClass("ConditionFXSound")]
    public class UConditionFXSound : UFXSound
    {
    }

    [UnrealClass("ConditionFXSoundDamageType")]
    public class UConditionFXSoundDamageType : UConditionFXSound
    {
    }

    [UnrealClass("EntityFXParticle")]
    public class UEntityFXParticle : UFXParticle
    {
    }

    [UnrealClass("FXAnimatedActor")]
    public class UFXAnimatedActor : UGameFX
    {
    }

    [UnrealClass("EntityFXAnimatedActor")]
    public class UEntityFXAnimatedActor : UFXAnimatedActor
    {
    }

    [UnrealClass("EntityFXAnimation")]
    public class UEntityFXAnimation : UFXAnimation
    {
    }

    [UnrealClass("EntityFXAnimationComplex")]
    public class UEntityFXAnimationComplex : UFXAnimation
    {
    }

    [UnrealClass("FXBeam")]
    public class UFXBeam : UGameFX
    {
    }

    [UnrealClass("EntityFXBeam")]
    public class UEntityFXBeam : UFXBeam
    {
    }

    [UnrealClass("EntityFXCameraShake")]
    public class UEntityFXCameraShake : UFXCameraShake
    {
    }

    [UnrealClass("EntityFXDecal")]
    public class UEntityFXDecal : UFXDecal
    {
    }

    [UnrealClass("EntityFXMaterialParameter")]
    public class UEntityFXMaterialParameter : UFXMaterialParameter
    {
    }

    [UnrealClass("EntityFXMeshAttachment")]
    public class UEntityFXMeshAttachment : UFXMeshAttachment
    {
    }

    [UnrealClass("EntityFXMeshScale")]
    public class UEntityFXMeshScale : UFXMeshScale
    {
    }

    [UnrealClass("EntityFXMeshSwap")]
    public class UEntityFXMeshSwap : UFXMeshSwap
    {
    }

    [UnrealClass("FXPhysicalForce")]
    public class UFXPhysicalForce : UGameFX
    {
    }

    [UnrealClass("EntityFXPhysicalForce")]
    public class UEntityFXPhysicalForce : UFXPhysicalForce
    {
    }

    [UnrealClass("EntityFXSound")]
    public class UEntityFXSound : UFXSound
    {
    }

    [UnrealClass("EntityFXSoundGroundMaterial")]
    public class UEntityFXSoundGroundMaterial : UEntityFXSound
    {
    }

    [UnrealClass("FXAnimatedActorMaterialParameter")]
    public class UFXAnimatedActorMaterialParameter : UFXMaterialParameter
    {
    }

    [UnrealClass("FXKnockables")]
    public class UFXKnockables : UGameFX
    {
    }

    [UnrealClass("InWorldTextComponent")]
    public class UInWorldTextComponent : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompAnimationComplex")]
    public class UGameEntityCompAnimationComplex : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompDeathFX")]
    public class UGameEntityCompDeathFX : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompSounds")]
    public class UGameEntityCompSounds : UActorComponent
    {
        [PropertyField]
        public UArray<int> BanterTargets { get; set; }
    }

    [UnrealClass("MarvelGFxFloatingNumberComp")]
    public class UGameGFxFloatingNumberComp : UActorComponent
    {
    }

    [UnrealClass("MarvelDecalComponent")]
    public class UGameDecalComponent : UDecalComponent
    {
    }

    [UnrealClass("MarvelGFxActorComp")]
    public class UGameGFxActorComp : UActorComponent
    {
    }

    [UnrealClass("MarvelGFxActorTooltipComp")]
    public class UGameGFxActorTooltipComp : UGameGFxActorComp
    {
    }

    [UnrealClass("MarvelEntityCompAnimationSimple")]
    public class UGameEntityCompAnimationSimple : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompBossIndicator")]
    public class UGameEntityCompBossIndicator : UEntityFXParticle
    {
    }

    [UnrealClass("MarvelEntityCompBuddyIndicator")]
    public class UGameEntityCompBuddyIndicator : UEntityFXParticle
    {
    }

    [UnrealClass("MarvelEntityCompInteractIndicator")]
    public class UGameEntityCompInteractIndicator : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompInteractOverlay")]
    public class UGameEntityCompInteractOverlay : UEntityFXParticle
    {
    }

    [UnrealClass("MarvelEntityCompMissionMarker")]
    public class UGameEntityCompMissionMarker : UGameDecalComponent
    {
    }

    [UnrealClass("MarvelEntityCompObjectiveMarker")]
    public class UGameEntityCompObjectiveMarker : UEntityFXDecal
    {
    }

    [UnrealClass("MarvelEntityCompPlayerIndicator")]
    public class UGameEntityCompPlayerIndicator : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompPlayerTargetIndicator")]
    public class UGameEntityCompPlayerTargetIndicator : UEntityFXParticle
    {
    }

    [UnrealClass("MarvelEntityCompPowers")]
    public class UGameEntityCompPowers : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompSpawnFX")]
    public class UGameEntityCompSpawnFX : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompTargetIndicator")]
    public class UGameEntityCompTargetIndicator : UEntityFXParticle
    {
    }

    [UnrealClass("MarvelEntityCompTargetLockOnIndicator")]
    public class UGameEntityCompTargetLockOnIndicator : UGameEntityCompPlayerTargetIndicator
    {
    }

    [UnrealClass("MarvelEntityCompThrowable")]
    public class UGameEntityCompThrowable : UActorComponent
    {
    }

    [UnrealClass("MarvelEntityCompTurret")]
    public class UGameEntityCompTurret : UActorComponent
    {
    }

    [UnrealClass("MarvelGFxActorChatBubbleComp")]
    public class UGameGFxActorChatBubbleComp : UGameGFxActorComp
    {
    }

    [UnrealClass("MarvelGFxActorStatusComp")]
    public class UGameGFxActorStatusComp : UGameGFxActorComp
    {
    }

    [UnrealClass("PowerFXAnimation")]
    public class UPowerFXAnimation : UFXAnimation
    {
    }

    [UnrealClass("PowerFXAnimationLooping")]
    public class UPowerFXAnimationLooping : UPowerFXAnimation
    {
    }

    [UnrealClass("PowerFXMeshAttachment")]
    public class UPowerFXMeshAttachment : UFXMeshAttachment
    {
    }

    [UnrealClass("MarvelPPComponent")]
    public class UGamePPComponent : UActorComponent
    {
    }

    [UnrealClass("MarvelPP_CinematicTransition")]
    public class UGamePP_CinematicTransition : UGamePPComponent
    {
    }

    [UnrealClass("MarvelPP_EscMenu")]
    public class UGamePP_EscMenu : UGamePPComponent
    {
    }

    [UnrealClass("MarvelPP_FadeInFadeOut")]
    public class UGamePP_FadeInFadeOut : UGamePPComponent
    {
    }

    [UnrealClass("MarvelPP_NearDeath")]
    public class UGamePP_NearDeath : UGamePPComponent
    {
    }

    [UnrealClass("MarvelPP_StageLock")]
    public class UGamePP_StageLock : UGamePPComponent
    {
    }

    [UnrealClass("MarvelVisualSwapComp")]
    public class UGameVisualSwapComp : UActorComponent
    {
    }

    [UnrealClass("MarvelUIComp")]
    public class UGameUIComp : UActorComponent
    {
    }

    [UnrealClass("MarvelUIDecalComponent")]
    public class UGameUIDecalComponent : UDecalComponent
    {
    }

    [UnrealClass("MarvelUIPrimaryResourceComp")]
    public class UGameUIPrimaryResourceComp : UGameUIComp
    {
    }

    [UnrealClass("MarvelUISecondaryResourceComp")]
    public class UGameUISecondaryResourceComp : UGameUIComp
    {
    }

    [UnrealClass("NaviFragmentRenderComponent")]
    public class UNaviFragmentRenderComponent : UPrimitiveComponent
    {
    }

    [UnrealClass("NaviPathRenderingComponent")]
    public class UNaviPathRenderingComponent : UPrimitiveComponent
    {
    }

    [UnrealClass("NaviRenderingComponent")]
    public class UNaviRenderingComponent : UPrimitiveComponent
    {
    }

    [UnrealClass("PowerFXAnimatedActor")]
    public class UPowerFXAnimatedActor : UFXAnimatedActor
    {
    }

    [UnrealClass("PowerFXAnimatedActorMaterialParameter")]
    public class UPowerFXAnimatedActorMaterialParameter : UFXAnimatedActorMaterialParameter
    {
    }

    [UnrealClass("PowerFXAttachmentMaterialParameter")]
    public class UPowerFXAttachmentMaterialParameter : UFXAttachmentMaterialParameter
    {
    }

    [UnrealClass("PowerFXAttachmentMaterialSwap")]
    public class UPowerFXAttachmentMaterialSwap : UGameFX
    {
    }

    [UnrealClass("PowerFXBeam")]
    public class UPowerFXBeam : UFXBeam
    {
    }

    [UnrealClass("PowerFXCameraManipulation")]
    public class UPowerFXCameraManipulation : UGameFX
    {
    }

    [UnrealClass("PowerFXCameraShake")]
    public class UPowerFXCameraShake : UFXCameraShake
    {
    }

    [UnrealClass("PowerFXCameraTeleport")]
    public class UPowerFXCameraTeleport : UPowerFXCameraManipulation
    {
    }

    [UnrealClass("PowerFXDecal")]
    public class UPowerFXDecal : UFXDecal
    {
    }

    [UnrealClass("PowerFXHide")]
    public class UPowerFXHide : UFXHide
    {
    }

    [UnrealClass("PowerFXLevitateObject")]
    public class UPowerFXLevitateObject : UGameFX
    {
    }

    [UnrealClass("PowerFXMaterialParameter")]
    public class UPowerFXMaterialParameter : UFXMaterialParameter
    {
    }

    [UnrealClass("PowerFXMeshMove")]
    public class UPowerFXMeshMove : UGameFX
    {
    }

    [UnrealClass("PowerFXMeshScale")]
    public class UPowerFXMeshScale : UFXMeshScale
    {
    }

    [UnrealClass("PowerFXMeshSwap")]
    public class UPowerFXMeshSwap : UFXMeshSwap
    {
    }

    [UnrealClass("PowerFXParticle")]
    public class UPowerFXParticle : UFXParticle
    {
    }

    // PowerFXHit / PowerFXHit_Crit are on-target impact VFX components that hang
    // off a power's class default object. They share UComponent's binary layout
    // (16-byte template prefix before properties) — without these registrations
    // the parser falls back to UObject.ReadBuffer and misaligns by 12 bytes,
    // causing "Index (FFFFFFFF) out of range" on the next property name read.
    [UnrealClass("PowerFXHit")]
    public class UPowerFXHit : UFXParticle
    {
    }

    [UnrealClass("PowerFXHit_Crit")]
    public class UPowerFXHitCrit : UPowerFXHit
    {
    }

    [UnrealClass("PowerFXPhysicalForce")]
    public class UPowerFXPhysicalForce : UFXPhysicalForce
    {
    }

    [UnrealClass("PowerFXPhysicsWeight")]
    public class UPowerFXPhysicsWeight : UFXPhysicsWeight
    {
    }

    [UnrealClass("PowerFXPostProcessing")]
    public class UPowerFXPostProcessing : UFXPostProcessing
    {
    }

    [UnrealClass("PowerFXProjectile")]
    public class UPowerFXProjectile : UGameFX
    {
    }

    [UnrealClass("PowerFXSound")]
    public class UPowerFXSound : UFXSound
    {
    }

    [UnrealClass("PowerFXSoundDamageType")]
    public class UPowerFXSoundDamageType : UPowerFXSound
    {
    }

    [UnrealClass("PowerFXSoundGroundMaterial")]
    public class UPowerFXSoundGroundMaterial : UPowerFXSound
    {
    }

    [UnrealClass("PowerFXTurretAim")]
    public class UPowerFXTurretAim : UGameFX
    {
    }

    [UnrealClass("ProjectileFXAnimation")]
    public class UProjectileFXAnimation : UGameFX
    {
    }

    [UnrealClass("ProjectileFXBeam")]
    public class UProjectileFXBeam : UFXBeam
    {
    }

    [UnrealClass("ProjectileFXCameraShake")]
    public class UProjectileFXCameraShake : UFXCameraShake
    {
    }

    [UnrealClass("ProjectileFXDecal")]
    public class UProjectileFXDecal : UFXDecal
    {
    }

    [UnrealClass("ProjectileFXMaterialParameter")]
    public class UProjectileFXMaterialParameter : UFXMaterialParameter
    {
    }

    [UnrealClass("ProjectileFXParticle")]
    public class UProjectileFXParticle : UFXParticle
    {
    }

    [UnrealClass("ProjectileFXPhysicalForce")]
    public class UProjectileFXPhysicalForce : UFXPhysicalForce
    {
    }

    [UnrealClass("ProjectileFXSound")]
    public class UProjectileFXSound : UFXSound
    {
    }

    [UnrealClass("TaserTrapComponent")]
    public class UTaserTrapComponent : UActorComponent
    {
    }

    [UnrealClass("TickableActorComponent")]
    public class UTickableActorComponent : UActorComponent
    {
    }
}

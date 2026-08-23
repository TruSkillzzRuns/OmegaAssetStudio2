using System.Collections.ObjectModel;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

public sealed class MaterialOverrideViewModel : MaterialToolViewModelBase
{
    private MhMaterialInstance? selectedMaterial;
    private MhMaterialOverrideProfile? selectedProfile;

    public MaterialOverrideViewModel()
    {
        Title = "Material Override Tool";
        Profiles = [];
    }

    public ObservableCollection<MhMaterialOverrideProfile> Profiles { get; }

    public MhMaterialInstance? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (!SetProperty(ref selectedMaterial, value))
                return;

            Profiles.Clear();
            if (selectedMaterial is null)
                return;

            foreach (MhMaterialOverrideProfile profile in selectedMaterial.OverrideProfiles)
                Profiles.Add(profile);
        }
    }

    public MhMaterialOverrideProfile? SelectedProfile
    {
        get => selectedProfile;
        set => SetProperty(ref selectedProfile, value);
    }
}


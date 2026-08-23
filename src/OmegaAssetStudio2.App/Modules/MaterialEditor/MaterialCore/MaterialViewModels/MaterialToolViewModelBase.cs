using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

public abstract class MaterialToolViewModelBase : INotifyPropertyChanged
{
    private bool isBusy;
    private string statusText = string.Empty;
    private string title = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MaterialEditorContext? Context { get; private set; }

    public bool IsBusy
    {
        get => isBusy;
        protected set => SetProperty(ref isBusy, value);
    }

    public string StatusText
    {
        get => statusText;
        protected set => SetProperty(ref statusText, value);
    }

    public string Title
    {
        get => title;
        protected set => SetProperty(ref title, value);
    }

    public void AttachContext(MaterialEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


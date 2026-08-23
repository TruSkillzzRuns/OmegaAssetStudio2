using System.Windows.Input;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialCommands;

public sealed class SwitchShaderPermutationCommand : ICommand
{
    private EventHandler? canExecuteChanged;
    private readonly Action execute;

    public SwitchShaderPermutationCommand(Action execute)
    {
        this.execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => canExecuteChanged += value;
        remove => canExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();

    public void NotifyCanExecuteChanged() => canExecuteChanged?.Invoke(this, EventArgs.Empty);
}


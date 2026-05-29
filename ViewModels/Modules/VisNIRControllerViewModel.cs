using ShockUI.Models.OpticalModules;
using ShockUI.Services.OpticalModules;

namespace ShockUI.ViewModels.Modules;

public sealed class VisNIRControllerViewModel : OpticalModuleControllerViewModel
{
    public VisNIRControllerViewModel(OpticalModuleSerialService serialService)
        : base(OpticalModuleDefinition.VisNIR, serialService)
    {
        ModuleTitle = "VisNir Controller";
    }
}
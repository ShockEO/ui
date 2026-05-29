using ShockUI.Models.OpticalModules;
using ShockUI.Services.OpticalModules;

namespace ShockUI.ViewModels.Modules;

public sealed class SwirControllerViewModel : OpticalModuleControllerViewModel
{
    public SwirControllerViewModel(OpticalModuleSerialService serialService)
        : base(OpticalModuleDefinition.SWIR, serialService)
    {
        ModuleTitle = "SWIR Controller";
    }
}
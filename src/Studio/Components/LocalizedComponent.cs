using Microsoft.AspNetCore.Components;
namespace Studio.Components;
public class LocalizedComponent : ComponentBase { [CascadingParameter] public bool English { get; set; } }

using System;
using System.Collections.Generic;
using System.Text;
using CodeFirstWebFramework;

namespace Electricity {
    [Implementation(typeof(AdminHelper))]   // This pulls in the default behaviour from CodeFirstWebFramework, which we can override
    public class Admin : AppModule {
        protected override void Init() {
            base.Init();
            InsertMenuOptions(
                new MenuOption("List Scenarios", "/home/list"),
                new MenuOption("New Scenario", "/home/view?id=0"),
                new MenuOption("Import", "/home/import"),
                new MenuOption("Check For Missing Data", "/home/check"),
                new MenuOption("Settings", "/admin/editsettings")
                );
        }
    }
}

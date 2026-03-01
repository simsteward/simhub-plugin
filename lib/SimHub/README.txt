Place SimHub SDK DLLs here to build the plugin:
- SimHub.Plugins.dll
- GameReaderCommon.dll

Copy from your SimHub installation folder (e.g. C:\Program Files (x86)\SimHub\).

Without these, the project still compiles but the plugin entry point (SimStewardPlugin) will not implement IPlugin/IDataPlugin and SimHub will not load it.

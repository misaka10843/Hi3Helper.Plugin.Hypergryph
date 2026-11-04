# Endfield parent process

The plugin stages a matching-signer executable as
`<game>/_Hi3_Hg/parent/<PID>/<session>/Games.exe`, creates it suspended, and
starts the official game executable with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`.
The staged parent is never resumed. Its handles and session files are released
on exit, startup failure or cancellation. Each launch owns a separate session.

Configure `parentExecutablePath` in `_Hi3_Hg/config.json` or the launcher's
plugin settings page. Empty means `<game>/PlatformProcess.exe`. A nonempty
value is an absolute or game-relative EXE path. There is no recursive search,
launcher download, or automatic direct-start fallback. Errors go to the plugin
log and the host's error channel.

Both the official game EXE and source must pass Windows Authenticode validation.
The SHA-256 fingerprints of their signer certificates must match exactly; the
staged copy is checked again before suspension. No publisher-name whitelist or
persisted certificate fingerprint is used. Changing the parent source does not
replace the actual game executable.

The original proposal used Endfield.exe itself as the default source. Testing
on the installed client returned Windows error 740 (elevation required) when
creating that image suspended from a normal process, so the default is the
single fixed PlatformProcess.exe source. Configuring an image that requires
elevation produces an explicit Windows error; the plugin does not elevate it.

Signature/suspension tests do not establish that a future game client accepts
the parent. Test a real launch after client updates. The plugin cannot remove
the game's own elevation or integrity requirements.

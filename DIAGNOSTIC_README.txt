Glide 4.1.4 crash diagnostic build
==================================

This build always writes a small live breadcrumb trace to:

  %USERPROFILE%\Downloads\Glide-Diagnostic-Latest.txt

The file is flushed continuously, so it should still contain the last Home/slideshow/navigation/render events even if Glide terminates without a managed exception.

If Glide crashes and you reopen it before collecting the file, the previous abnormal trace is preserved as:

  %USERPROFILE%\Downloads\Glide-Diagnostic-Previous-AbnormalExit.txt

Please drag the relevant TXT file back into ChatGPT after reproducing the crash.

4.1.4 also serializes background refinement presentation with Home/manual/slideshow navigation and waits for the replacement frame to cross the compositor fence before retired bitmap/cache ownership can be released.

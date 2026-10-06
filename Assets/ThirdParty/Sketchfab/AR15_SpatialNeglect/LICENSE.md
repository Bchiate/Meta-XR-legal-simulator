AR-15 style rifle — by SpatialNeglect (@jeandiz)
https://sketchfab.com/3d-models/ar-15-style-rifle-50d33435445e439c95e3b36e9d4bd798
Downloaded 2026-10-03 (source archive, 82 MB) with Brett's Sketchfab login, at his request.
License: CC Attribution (CC BY 4.0) — https://creativecommons.org/licenses/by/4.0/. The author replaced real company logos with
fictional ones ("X4A1", made-up MFR/PART/SERIAL plate) to allow commercial use. Credit required in the game's credits screen:
"AR-15 style rifle" (https://sketchfab.com/3d-models/ar-15-style-rifle-50d33435445e439c95e3b36e9d4bd798) by SpatialNeglect (https://sketchfab.com/jeandiz) is licensed under Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/). Modified: optic, GPS and sling strap removed, red-dot sight added, textures resized, magazine window made opaque.
Call it "AR-15" / "Rifle" in game, never a brand name.

CC BY 4.0 §3(a)(1)(B) requires saying the work was modified, so keep the "Modified: …" clause and the license link in the credits line.

Files (prep: ArtSource/AR15_SpatialNeglect/prep/, parts map in prep/parts.md):
- AR15.fbx — the author's "ar style gun.fbx" with the GPS texture's local path / brand-name strings neutralised (fbx_scrub_paths.py);
  geometry, nodes, materials and connections are byte-identical. It still contains the acog, gps and belt meshes: the builder deletes them.
- Textures/ — make_textures.py: AR15_1_* (material "ar15 1") and AR15_2_* (material "ar 15 2"), BaseColor + Normal at 2048, the rest
  at 1024; normals are OpenGL (+Y), no flip. AR15_2_BaseColor has the rounds baked into the magazine window. Bullets_* (512) are only
  needed if the "bullets" mesh is kept.

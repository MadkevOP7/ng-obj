# No GameObject Workflow (NObject)

## Background
Unity's GameObject representation of objects poses an optimization problem for devs working on large open-world multiplayer games. Networked GameObjects take up memory even if not being rendered, and the cost of Unity's Instantiate() and Destroy() introduces tremendous load time for large levels. Upon developing Limen, an open-world co-op horror game, I faced problem optmizing my game's runtime performance and two minute load time, as the open-world level contained 1 million interactable trees.

Sure, there are other solutions such as breaking up the level to multiple scenes, but that would introduce a lot of complexity to development and the approach is also very error prone for networked games, thus, comes NObject. 

NObject serializes networked GameObjects to internal lightweight data representations, and utilizes spatial hash grid to automatically load and unload data, where rendering data is directly fed to GPU buffer and networking functions are maintained. 

Written by Kevin Yang (MADKEV Studio)

## Dependencies
- .Net 6.x
- Unity
- Mirror (Networking Library)
- GPUInstancer (GurBu)



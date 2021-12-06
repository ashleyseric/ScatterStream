# Scatter Stream

A runtime object scattering/vegetation authoring, streaming and rendering tool for Unity optimised for instanced rendering a very large number of placed items.

Scatter Stream works entirely independent of Unity's Terrain system although items can be placed on terrains if desired. 


## Still in early development

Scatter Stream is still under active development and I expect various aspects are likely to change significantly in future so use at your own risk for the time being.

The following features are currently supported with many more on the way:

- Entirely runtime compatible, no editor requried.
- Painting, erasing and individual item placement.
- Automatic streaming of tiles to/from disk.
    - Optional events to hook into to expand streaming functionality with a remote server.
- Multi-stream support:
    - Multiple streams with unique parameters, brush and preset collections.
    - Per-stream parent transform which affects all placed items in realtime.
    - Per-stream ECS or Instanced Rendering render mode option.
    - Freely switch current editing stream.
	- Various per-stream optimisation parameters.
- Automatically ingest a GameObject hierarchy into a scatter preset in one click.
    - Automatic billboard capture/generation with directionality.
    - Customisable level of detail parameters.


## License

Scatter Stream is available under the MIT License.  See [LICENSE.md](LICENSE.md) LICENSE.md for details.
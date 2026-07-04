# Card Replace Packs

Each direct child folder is an art pack. A pack must contain `pack.json`.

`enabledDefault` controls the first generated config value. `priorityDefault` controls conflict order. Higher priority wins because packs are merged from low priority to high priority.

Example:

```json
{
  "id": "my_pack",
  "name": "My Pack",
  "enabledDefault": true,
  "priorityDefault": 100,
  "overrides": [
    {
      "source_path": "res://images/packed/card_portraits/regent/strike_regent.png",
      "type": "static",
      "file": "images/strike_regent.png"
    }
  ]
}
```

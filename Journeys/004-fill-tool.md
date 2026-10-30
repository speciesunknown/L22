# 004 Fill Tool

AS a user
I WANT TO fill certain areas with a paint fill
SO THAT i can paint large areas quickly

## Scenarios

### Terrain type

GIVEN I have elevation selected
WHEN i click a tile
THEN I click in a tile
THEN that whole tile is filled to that elevation

GIVEN I have terrain type selected
WHEN i click a tile
THEN I click in a tile
THEN that whole tile is filled to that terrain type


### Fill type

GIVEN I have fill type "tile fill" selected
WHEN I click a tile
THEN that tile is filled

GIVEN I have fill type "area fill" selected
WHEN I click a tile with data already in it
THEN the fill spreads at most to the edge of existing tiles
AND it stops at tiles painted dis-similarly to the current fill


# 009 city builder



Cities are tile based. Each tile is 12x12 feet.



The four corners of a tile elevate themselves automatically to match terrain. they use a root node, which can be rotated and placed arbitrary

from there, the city tile builder presents options. lets start out with 
 - brick floor tiles
 - straight road tiles (n/s, e/w)
 - T junction road tiles (n, e, s, w)
 - crossroad road tiles
 - wall tiles (N, E, S, W) 
 - gates (N, E, S, W)

 Edges of these tiles should logically match, for example a T junction has sidewalk stuf fin the logical places

To keep it simple for now, just make them out of super primitive looking meshes. we'll create symbolic textures later


# Journeys

## Placing new cities

GIVEN I have city mode set
AND I do not have a tile selected
THEN I get options to place the first tile which can be any tile in the right menu

GIVEN I am in tile mode
AND I do not have any tiles selected
WHEN I click
THEN I add the selected tile from the right menu
AND its neighbours (north, east, south, west relative to the tile) show up as pink tile-sized cubes

GIVEN I have an unset neighbour selected
WHEN I click one of the allowed options on the right
THEN a tile of that type is selected
AND the new tile becomes the active selection

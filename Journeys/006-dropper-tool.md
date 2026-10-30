# 006 dropper tool

AS a user
I WANT to select an area of the ground
SO THAT I can pick up the properties of that layer
AND continue to paint that somewhere else



# Scenarios

GIVEN I am in elevation or terrain type mode
WHEN I hover over the top down view
THEN I get a paint dropper as my cursor

GIVEN I am in dropper mode

...
WHEN I click an area with the left mouse button
THEN the properties of that area are remembered


## Terrain Type Mode
GIVEN I am in terrain type mode
AND I paint
THEN the settings I paint dropped will be used

# 003 Elevated Corners

# Mission Statement

AS a user of the application 
I WANT to elevate the corners of a chunk
SO THAT my chunks follow gradual elevation even when I don't paint gradients

# Scenarios

GIVEN I have a scene loaded
WHEN I click the Corner Elevation tool
THEN I see the corners highlighted pink
AND I get a mouse pointer which is a box select symbol
AND corners in the editor will elevate all touching corners of chunks.

GIVEN I have Corner Elevation active
WHEN I box select one or more corners
THEN the context menu at the right allows me to enter a number, then add to, or override the elevation

GIVEN a chunk with a corner shared with one or more other chunks
WHEN I elevate the corner
THEN the other chunks sharing that corner follow

## Selection Logic
GIVEN I have a selection of one or more corners

...
WHEN I click away
THEN all selections go

...
WHEN I shift + drag over one or more points
THEN unselected points get selected
AND selected points get unselected

WHEN I drag over zero  or more points WITHOUT shift
THEN my new selection replaces the current selection

...
WHEN I make a change
THEN the current selection stays

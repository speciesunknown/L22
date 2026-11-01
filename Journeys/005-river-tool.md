# 005 River Tool

AS a user
I WANT TO draw rivers using bezier curves
SO THAT they can be curvy just like a real river or stream


# Scenarios
WHEN I click the river tool
THEN I am able to add a new one or select an existing point

GIVEN I am in river mode
AND I have a point selected on a river
WHEN I click again somewhere new
THEN a new branch comes out to meet the new point I've painted
AND it defaults to the flow direction of the node I selected

## Editing nodes on the river
GIVEN I have one or more vertices of a river selected

...
WHEN I increase the width of the river
THEN it changes that node and its two neighbours

...
WHEN I change the direction of those vertices
THEN the selected nodes change direction

WHEN the world is rendered
THEN the terrain affected by the river is pushed down to indicate erosion
AND the triangles near the river are broken down so the edges follow the river cleanly

GIVEN a file has riverbeds in it
WHEN I export the file
THEN triangles which have been swapped to a riverbed mesh are marked out
AND a mesh is created for the suppressed bed
AND the vertices are shared correctly with non-mesh triangles


GIVEN I am in river mode
WHEN I click an existing node
THEN it is selected
AND I can edit the curves and weights by dragging one of the handles around
AND it's radius from the center is it's weighting


WHEN I click on the map
THEN I will be starting a new river



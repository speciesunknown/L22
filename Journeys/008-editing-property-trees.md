# 008 Editing property trees

GIVEN I have a property tree open
WHEN I hover over the expand button on a node
THEN I get a pointer cursor

WHEN I mousedown on a node
THEN it opens or closes

WHEN I edit any element of a tree
AND there is still at least one node at the end
THEN the expanded parts remain open

GIVEN a text box has focus
WHEN I press tab or enter
THEN I move to the next box:
 - when it's a name of a property, i move directly to the value
 - when its a value, it moves to the name field of the next property, if any


GIVEN I have a node (including the root) with more than one entry
WHEN I hover over the right side of each node
THEN I see an UP and Down arrow button


WHEN I click an up or down arrow
THEN that element in the array moves up and down



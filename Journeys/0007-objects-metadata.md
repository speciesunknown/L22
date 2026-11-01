# 007 Objects and metadata

GIVEN I am in object mode
WHEN I click an object
THEN its properties are shown on the right
AND the object list navigates to it

GIVEN I am in object mode
WHEN I drag an object
THEN it moves with the mouse

GIVEN I am in object mode
WHEN I double click an object
THEN I get the modal to edit it's properties (which also allows me to change the prefab)

GIVEN an object has no prefab
WHEN I select it
THEN I see a searchable dropdown to set the prefab

GIVEN an object has a prefab
WHEN I select it
THEN it's prefab is selected
AND I see an "Edit" button next to the selection.a
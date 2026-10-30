# 007 Objects and metadata

Objects are used to specify where certain things are which are not used in the editor but are required for the game.

Objects need a string display name, string foreign_identifier, link to an object prefab, and an icon (select an image and optionally select a grid square. so i might enter that it is cell 0,1 of a 2x2 grid)

there should be an object browser in the right menu.

Object prefabs provide default values unless overridden by an instance

Properties on objects can be text, number, array, or Properties (i.e a tree node with more properties underneath)

Objects are drawn as billboard sprites in the 3d view. by default, if no image is provided, they're represented by a pink cube. 

in the 2d drawing view they're top down icons with no depth test. 

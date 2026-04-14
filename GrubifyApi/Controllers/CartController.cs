using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using GrubifyApi.Models;

namespace GrubifyApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CartController : ControllerBase
    {
        // Thread-safe in-memory cart storage (in production, use database or Redis with TTL)
        private static readonly ConcurrentDictionary<string, Cart> UserCarts = new();
        private static readonly ConcurrentDictionary<string, DateTime> CartLastAccessed = new();
        private const int MaxCartsInMemory = 1000;
        private static readonly TimeSpan CartExpiry = TimeSpan.FromMinutes(30);

        // Cache food items statically to avoid per-request allocation
        private static readonly List<FoodItem> CachedFoodItems = new()
        {
            new FoodItem { Id = 1, Name = "Margherita Pizza", Price = 16.99m, ImageUrl = "https://images.unsplash.com/photo-1604382354936-07c5d9983bd3?w=400&h=300&fit=crop", RestaurantId = 1 },
            new FoodItem { Id = 2, Name = "Chicken Alfredo", Price = 19.99m, ImageUrl = "https://images.unsplash.com/photo-1621996346565-e3dbc353d2e5?w=400&h=300&fit=crop", RestaurantId = 1 },
            new FoodItem { Id = 3, Name = "Caesar Salad", Price = 12.99m, ImageUrl = "https://images.unsplash.com/photo-1546793665-c74683f339c1?w=400&h=300&fit=crop", RestaurantId = 1 },
            new FoodItem { Id = 4, Name = "California Roll", Price = 14.99m, ImageUrl = "https://images.unsplash.com/photo-1579584425555-c3ce17fd4351?w=400&h=300&fit=crop", RestaurantId = 2 },
            new FoodItem { Id = 5, Name = "Spicy Tuna Roll", Price = 16.99m, ImageUrl = "https://images.unsplash.com/photo-1617196034796-73dfa7b1fd56?w=400&h=300&fit=crop", RestaurantId = 2 },
            new FoodItem { Id = 6, Name = "Chicken Teriyaki Bowl", Price = 18.99m, ImageUrl = "https://images.unsplash.com/photo-1546069901-eacef0df6022?w=400&h=300&fit=crop", RestaurantId = 2 },
            new FoodItem { Id = 7, Name = "Chicken Tikka Masala", Price = 17.99m, ImageUrl = "https://images.unsplash.com/photo-1565557623262-b51c2513a641?w=400&h=300&fit=crop", RestaurantId = 3 },
            new FoodItem { Id = 8, Name = "Vegetable Biryani", Price = 15.99m, ImageUrl = "https://images.unsplash.com/photo-1563379091339-03246963d17a?w=400&h=300&fit=crop", RestaurantId = 3 },
            new FoodItem { Id = 9, Name = "Garlic Naan", Price = 4.99m, ImageUrl = "https://images.unsplash.com/photo-1601050690597-df0568f70950?w=400&h=300&fit=crop", RestaurantId = 3 },
            new FoodItem { Id = 10, Name = "Classic Cheeseburger", Price = 13.99m, ImageUrl = "https://images.unsplash.com/photo-1568901346375-23c9450c58cd?w=400&h=300&fit=crop", RestaurantId = 4 },
            new FoodItem { Id = 11, Name = "Crispy Chicken Sandwich", Price = 15.99m, ImageUrl = "https://images.unsplash.com/photo-1606755962773-d324e9a13086?w=400&h=300&fit=crop", RestaurantId = 4 },
            new FoodItem { Id = 12, Name = "Sweet Potato Fries", Price = 6.99m, ImageUrl = "https://images.unsplash.com/photo-1573080496219-bb080dd4f877?w=400&h=300&fit=crop", RestaurantId = 4 },
            new FoodItem { Id = 13, Name = "Quinoa Buddha Bowl", Price = 14.99m, ImageUrl = "https://images.unsplash.com/photo-1512621776951-a57141f2eefd?w=400&h=300&fit=crop", RestaurantId = 5 },
            new FoodItem { Id = 14, Name = "Acai Berry Smoothie", Price = 8.99m, ImageUrl = "https://images.unsplash.com/photo-1553530666-ba11a7da3888?w=400&h=300&fit=crop", RestaurantId = 5 },
            new FoodItem { Id = 15, Name = "Grilled Salmon Salad", Price = 18.99m, ImageUrl = "https://images.unsplash.com/photo-1540420773420-3366772f4999?w=400&h=300&fit=crop", RestaurantId = 5 }
        };

        [HttpGet("{userId}")]
        public ActionResult<Cart> GetCart(string userId)
        {
            EvictExpiredCarts();
            var cart = UserCarts.GetOrAdd(userId, _ => new Cart { UserId = userId });
            CartLastAccessed[userId] = DateTime.UtcNow;
            return Ok(cart);
        }

        [HttpPost("{userId}/items")]
        public ActionResult<Cart> AddItemToCart(string userId, [FromBody] AddCartItemRequest request)
        {
            EvictExpiredCarts();
            var cart = UserCarts.GetOrAdd(userId, _ => new Cart { UserId = userId });
            CartLastAccessed[userId] = DateTime.UtcNow;

            lock (cart)
            {
                var existingItem = cart.Items.FirstOrDefault(i => i.FoodItemId == request.FoodItemId);

                if (existingItem != null)
                {
                    existingItem.Quantity += request.Quantity;
                    existingItem.SpecialInstructions = request.SpecialInstructions;
                }
                else
                {
                    var newItem = new CartItem
                    {
                        Id = cart.Items.Count + 1,
                        FoodItemId = request.FoodItemId,
                        FoodItem = GetFoodItemById(request.FoodItemId),
                        Quantity = request.Quantity,
                        SpecialInstructions = request.SpecialInstructions
                    };
                    cart.Items.Add(newItem);
                }
            }

            return Ok(cart);
        }

        [HttpPut("{userId}/items/{itemId}")]
        public ActionResult<Cart> UpdateCartItem(string userId, int itemId, [FromBody] UpdateCartItemRequest request)
        {
            if (!UserCarts.TryGetValue(userId, out var cart))
            {
                return NotFound("Cart not found");
            }

            lock (cart)
            {
                var item = cart.Items.FirstOrDefault(i => i.Id == itemId);

                if (item == null)
                {
                    return NotFound("Item not found in cart");
                }

                item.Quantity = request.Quantity;
                item.SpecialInstructions = request.SpecialInstructions;
            }

            return Ok(cart);
        }

        [HttpDelete("{userId}/items/{itemId}")]
        public ActionResult<Cart> RemoveItemFromCart(string userId, int itemId)
        {
            if (!UserCarts.TryGetValue(userId, out var cart))
            {
                return NotFound("Cart not found");
            }

            lock (cart)
            {
                var item = cart.Items.FirstOrDefault(i => i.Id == itemId);

                if (item == null)
                {
                    return NotFound("Item not found in cart");
                }

                cart.Items.Remove(item);
            }

            return Ok(cart);
        }

        [HttpDelete("{userId}")]
        public ActionResult ClearCart(string userId)
        {
            if (UserCarts.TryGetValue(userId, out var cart))
            {
                lock (cart)
                {
                    cart.Items.Clear();
                }
            }
            return Ok();
        }

        /// <summary>
        /// Evicts carts that haven't been accessed within the expiry window,
        /// and enforces a maximum cart count to prevent unbounded memory growth.
        /// Uses thread-safe TryRemove operations on ConcurrentDictionary.
        /// </summary>
        private static void EvictExpiredCarts()
        {
            var now = DateTime.UtcNow;
            var expiredUsers = CartLastAccessed
                .Where(kvp => now - kvp.Value > CartExpiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var userId in expiredUsers)
            {
                UserCarts.TryRemove(userId, out _);
                CartLastAccessed.TryRemove(userId, out _);
            }

            // If still over limit, evict oldest carts
            while (UserCarts.Count > MaxCartsInMemory && CartLastAccessed.Count > 0)
            {
                var oldest = CartLastAccessed.OrderBy(kvp => kvp.Value).FirstOrDefault().Key;
                if (oldest != null)
                {
                    UserCarts.TryRemove(oldest, out _);
                    CartLastAccessed.TryRemove(oldest, out _);
                }
                else
                {
                    break;
                }
            }
        }

        // Helper method to get food item from static cache (no per-request allocation)
        private static FoodItem GetFoodItemById(int foodItemId)
        {
            return CachedFoodItems.FirstOrDefault(f => f.Id == foodItemId) ?? new FoodItem();
        }
    }

    public class AddCartItemRequest
    {
        public int FoodItemId { get; set; }
        public int Quantity { get; set; }
        public string SpecialInstructions { get; set; } = string.Empty;
    }

    public class UpdateCartItemRequest
    {
        public int Quantity { get; set; }
        public string SpecialInstructions { get; set; } = string.Empty;
    }
}

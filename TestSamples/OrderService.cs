using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ECommerce.Services
{
    // ── Interfaces (external dependencies) ────────────────────────────────────

    public interface IOrderRepository
    {
        Task<Customer> GetCustomerAsync(int customerId);
        Task<Order> SaveOrderAsync(Order order);
        Task<bool> CheckStockAsync(int productId, int quantity);
    }

    public interface IEmailService
    {
        Task SendConfirmationAsync(string email, int orderId, decimal total);
    }

    public interface IPricingService
    {
        decimal ApplyDiscount(decimal price, string couponCode);
    }

    // ── Models ─────────────────────────────────────────────────────────────────

    public class Customer
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string TierCode { get; set; }
        public decimal DiscountRate { get; set; }
    }

    public class OrderRequest
    {
        public int CustomerId { get; set; }
        public List<LineItemRequest> Items { get; set; }
        public string CouponCode { get; set; }
    }

    public class LineItemRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public List<OrderLine> Lines { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Discount { get; set; }
        public decimal Total { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class OrderLine
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineTotal => UnitPrice * Quantity;
    }

    public class OrderResult
    {
        public bool Success { get; set; }
        public int? OrderId { get; set; }
        public decimal Total { get; set; }
        public string ErrorMessage { get; set; }
    }

    public enum OrderStatus { Pending, Confirmed, Shipped, Cancelled }

    // ── Main service ──────────────────────────────────────────────────────────

    public class OrderService
    {
        // Injected dependencies (inter-class flows originate here)
        private readonly IOrderRepository _repository;
        private readonly IEmailService _emailService;
        private readonly IPricingService _pricingService;

        // Configurable threshold
        private readonly decimal _freeShippingThreshold;
        private static readonly decimal TaxRate = 0.08m;

        public OrderService(
            IOrderRepository repository,
            IEmailService emailService,
            IPricingService pricingService,
            decimal freeShippingThreshold = 50m)
        {
            _repository = repository;
            _emailService = emailService;
            _pricingService = pricingService;
            _freeShippingThreshold = freeShippingThreshold;
        }

        // ── Primary entry point ───────────────────────────────────────────────

        public async Task<OrderResult> PlaceOrderAsync(OrderRequest request)
        {
            // Validate input
            if (request == null || request.Items == null || request.Items.Count == 0)
                return new OrderResult { Success = false, ErrorMessage = "Invalid request" };

            // Load customer — inter-class call, result flows into local
            var customer = await _repository.GetCustomerAsync(request.CustomerId);
            if (customer == null)
                return new OrderResult { Success = false, ErrorMessage = "Customer not found" };

            // Stock check for each line — loop with inter-class call
            foreach (var item in request.Items)
            {
                bool inStock = await _repository.CheckStockAsync(item.ProductId, item.Quantity);
                if (!inStock)
                    return new OrderResult { Success = false, ErrorMessage = $"Product {item.ProductId} out of stock" };
            }

            // Map request items to order lines (inter-method call)
            var lines = request.Items.Select(i => MapToOrderLine(i)).ToList();

            // Calculate financials (inter-method calls, data flows through)
            decimal subtotal = CalculateSubtotal(lines);
            decimal discount = CalculateDiscount(subtotal, customer.DiscountRate, request.CouponCode);
            decimal shipping = CalculateShipping(subtotal - discount);
            decimal tax = ApplyTax(subtotal - discount + shipping);
            decimal total = subtotal - discount + shipping + tax;

            // Build order entity
            var order = new Order
            {
                CustomerId = customer.Id,
                Lines = lines,
                Subtotal = subtotal,
                Discount = discount,
                Total = total,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            // Persist — inter-class call, result flows back
            var savedOrder = await _repository.SaveOrderAsync(order);

            // Notify — inter-class call, data from multiple locals flows in
            await _emailService.SendConfirmationAsync(customer.Email, savedOrder.Id, total);

            return new OrderResult
            {
                Success = true,
                OrderId = savedOrder.Id,
                Total = total
            };
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private OrderLine MapToOrderLine(LineItemRequest src)
        {
            return new OrderLine
            {
                ProductId = src.ProductId,
                Quantity = src.Quantity,
                UnitPrice = src.UnitPrice
            };
        }

        private decimal CalculateSubtotal(List<OrderLine> lines)
        {
            decimal subtotal = lines.Sum(l => l.LineTotal);
            return subtotal;
        }

        private decimal CalculateDiscount(decimal subtotal, decimal customerRate, string couponCode)
        {
            // Loyalty discount first
            decimal loyaltyDiscount = subtotal * customerRate;

            // Then coupon on the remainder (inter-class call)
            decimal afterLoyalty = subtotal - loyaltyDiscount;
            decimal afterCoupon = string.IsNullOrEmpty(couponCode)
                ? afterLoyalty
                : _pricingService.ApplyDiscount(afterLoyalty, couponCode);

            decimal totalDiscount = subtotal - afterCoupon;
            return totalDiscount;
        }

        private decimal CalculateShipping(decimal discountedSubtotal)
        {
            if (discountedSubtotal >= _freeShippingThreshold)
                return 0m;

            // Tiered shipping rates
            decimal rate = discountedSubtotal switch
            {
                < 20m => 8.99m,
                < 35m => 5.99m,
                _ => 3.99m
            };
            return rate;
        }

        private decimal ApplyTax(decimal taxableAmount)
        {
            decimal tax = taxableAmount * TaxRate;
            return tax;
        }

        // ── Static utility ────────────────────────────────────────────────────

        public static string FormatOrderSummary(OrderResult result)
        {
            if (!result.Success)
                return $"Order failed: {result.ErrorMessage}";
            return $"Order #{result.OrderId} placed. Total: {result.Total:C}";
        }
    }
}
